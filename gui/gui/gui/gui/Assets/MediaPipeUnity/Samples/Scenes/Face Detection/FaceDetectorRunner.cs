// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System.Collections;
using Mediapipe.Tasks.Vision.FaceDetector;
using UnityEngine;
using UnityEngine.Rendering;
using FaceDetectionResult = Mediapipe.Tasks.Components.Containers.DetectionResult;

namespace Mediapipe.Unity.Sample.FaceDetection
{
    public class FaceDetectorRunner : VisionTaskApiRunner<FaceDetector>
    {
        [SerializeField] private DetectionResultAnnotationController _detectionResultAnnotationController;

        // [NEW] Latency Measurement
        public float LastDetectionMs { get; private set; } = 0f;
        private long _lastAsyncTs = -1;
        private long _lastAsyncSendTicks = 0;

        private Experimental.TextureFramePool _textureFramePool;
        private bool _stopRequested = false;

        public FaceDetectionConfig config; // [FIX] Init in Awake to avoid Unity API warning
        public bool HasFace { get; private set; } = false;
        public UnityEngine.Rect LastFaceRectNorm { get; private set; } = new UnityEngine.Rect(0, 0, 0, 0);
        public FaceDetectionResult LastResult { get; private set; }
        public bool HasLastResult { get; private set; }

        private void Awake()
        {
            if (config == null) config = new FaceDetectionConfig();
        }


        public override void Stop()
        {
            // 1) Set stop flag first to exit Run() loop
            _stopRequested = true;

            // 2) Stop runner/coroutine/task pipeline (base.Stop also handles imageSource.Stop())
            base.Stop();

            // 3) Dispose frame pool
            _textureFramePool?.Dispose();
            _textureFramePool = null;

            // 4) Clear state to prevent UI/logic from using stale data
            HasFace = false;
            HasLastResult = false;
            LastFaceRectNorm = new UnityEngine.Rect(0, 0, 0, 0);
            LastResult = default;

            // Note: taskApi cleanup is handled by base.Stop()
        }

        protected override IEnumerator Run()
        {
            if (config == null) config = new FaceDetectionConfig();

            if (string.IsNullOrEmpty(config.ModelPath))
            {
                Debug.LogError($"FaceDetectorRunner: ModelPath is empty. ModelName={config.ModelName}");
                yield break;
            }

            var imageSource = ImageSourceProvider.ImageSource;
            if (imageSource == null)
            {
                Debug.LogError("FaceDetectorRunner: ImageSourceProvider.ImageSource is null (missing ImageSourceProvider/WebCamSource?)");
                yield break;
            }

            Debug.Log($"Delegate = {config.Delegate}");
            Debug.Log($"Image Read Mode = {config.ImageReadMode}");
            Debug.Log($"Model = {config.ModelName}");
            Debug.Log($"Running Mode = {config.RunningMode}");
            Debug.Log($"MinDetectionConfidence = {config.MinDetectionConfidence}");
            Debug.Log($"MinSuppressionThreshold = {config.MinSuppressionThreshold}");
            Debug.Log($"NumFaces = {config.NumFaces}");

            yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

            var options = config.GetFaceDetectorOptions(config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnFaceDetectionsOutput : null);
            taskApi = FaceDetector.CreateFromOptions(options, GpuManager.GpuResources);

            _stopRequested = false;
            yield return imageSource.Play();

            if (!imageSource.isPrepared)
            {
                Debug.LogError("Failed to start ImageSource, exiting...");
                yield break;
            }

            // Use RGBA32 as the input format.
            // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
            _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, 10);

            // NOTE: The screen will be resized later, keeping the aspect ratio.
            if (screen == null)
            {
                // Debug.LogError("FaceDetectorRunner: 'screen' is NULL. You probably didn't copy the full Solution hierarchy (Screen) into this scene.");
                // yield break;
                Debug.LogWarning("FaceDetectorRunner: 'screen' is NULL. Overlay rendering might be disabled, but detection continues.");
            }
            else
            {
                screen.Initialize(imageSource);
            }

            if (_detectionResultAnnotationController != null)
            {
                SetupAnnotationController(_detectionResultAnnotationController, imageSource);
            }

            var transformationOptions = imageSource.GetTransformationOptions();
            var flipHorizontally = transformationOptions.flipHorizontally;
            var flipVertically = transformationOptions.flipVertically;
            var imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformationOptions.rotationAngle);

            AsyncGPUReadbackRequest req = default;
            var waitUntilReqDone = new WaitUntil(() => req.done);
            var waitForEndOfFrame = new WaitForEndOfFrame();
            var result = FaceDetectionResult.Alloc(options.numFaces);

            // NOTE: we can share the GL context of the render thread with MediaPipe (for now, only on Android)
            var canUseGpuImage = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && GpuManager.GpuResources != null;
            using var glContext = canUseGpuImage ? GpuManager.GetGlContext() : null;

            while (!_stopRequested)
            {
                if (isPaused)
                {
                    yield return new WaitWhile(() => isPaused);
                }
                if (_stopRequested) break;

                if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return null;
                    continue;
                }

                // Build the input Image
                Image image;
                switch (config.ImageReadMode)
                {
                    case ImageReadMode.GPU:
                        if (!canUseGpuImage)
                        {
                            throw new System.Exception("ImageReadMode.GPU is not supported");
                        }
                        textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                        image = textureFrame.BuildGPUImage(glContext);
                        // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
                        // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
                        yield return waitForEndOfFrame;
                        break;
                    case ImageReadMode.CPU:
                        yield return waitForEndOfFrame;
                        textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();
                        break;
                    case ImageReadMode.CPUAsync:
                    default:
                        req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                        yield return waitUntilReqDone;

                        if (req.hasError)
                        {
                            Debug.LogWarning($"Failed to read texture from the image source");
                            continue;
                        }
                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();
                        break;
                }

                switch (taskApi.runningMode)
                {
                    case Tasks.Vision.Core.RunningMode.IMAGE:
                        long t0_img = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool ok_img = taskApi.TryDetect(image, imageProcessingOptions, ref result);
                        LastDetectionMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0_img) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

                        if (ok_img)
                        {
                            LastResult = result;
                            HasLastResult = true;
                            if (_detectionResultAnnotationController != null) _detectionResultAnnotationController.DrawNow(result);
                        }
                        else
                        {
                            // clear the annotation
                            HasLastResult = false;
                            if (_detectionResultAnnotationController != null) _detectionResultAnnotationController.DrawNow(default);
                        }
                        break;
                    case Tasks.Vision.Core.RunningMode.VIDEO:
                        long t0_vid = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool ok_vid = taskApi.TryDetectForVideo(image, GetCurrentTimestampMillisec(), imageProcessingOptions, ref result);
                        LastDetectionMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0_vid) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

                        if (ok_vid)
                        {
                            LastResult = result;
                            HasLastResult = true;
                            if (_detectionResultAnnotationController != null) _detectionResultAnnotationController.DrawNow(result);
                        }
                        else
                        {
                            // clear the annotation
                            HasLastResult = false;
                            if (_detectionResultAnnotationController != null) _detectionResultAnnotationController.DrawNow(default);
                        }
                        break;
                    case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
                        var ts = GetCurrentTimestampMillisec();
                        _lastAsyncTs = ts;
                        _lastAsyncSendTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        taskApi.DetectAsync(image, ts, imageProcessingOptions);
                        break;
                }
            }
        }

        private void OnFaceDetectionsOutput(FaceDetectionResult result, Image image, long timestamp)
        {
            if (timestamp == _lastAsyncTs && _lastAsyncSendTicks > 0)
            {
                LastDetectionMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - _lastAsyncSendTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }

            LastResult = result;
            HasLastResult = true;
            if (_detectionResultAnnotationController != null) _detectionResultAnnotationController.DrawLater(result);
        }

        private void OnDisable()
        {
            Stop();
        }

        private void OnDestroy()
        {
            Stop();
        }
    }
}
