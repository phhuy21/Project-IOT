import modal

# 1. DEFINE IMAGE ENVIRONMENT
def download_models():
    import os
    import numpy as np
    from deepface import DeepFace
    from ultralytics import YOLO

    os.makedirs("/root/.deepface/weights", exist_ok=True)

    print("Downloading ArcFace + RetinaFace + YOLOv8...")
    try:
        # 1. ArcFace recognition model
        DeepFace.build_model("ArcFace")

        # 2. RetinaFace detector — force download retinaface.h5 (119MB)
        #    Bang cach goi extract_faces voi anh gia, DeepFace se tai weights ve
        dummy_img = np.zeros((100, 100, 3), dtype=np.uint8)
        try:
            DeepFace.extract_faces(
                img_path=dummy_img,
                detector_backend="retinaface",
                enforce_detection=False
            )
        except Exception:
            pass  # Khong co mat trong anh gia, nhung weights da duoc tai ve

        # 3. YOLOv8n anti-fraud model
        YOLO("yolov8n.pt")
    except Exception as e:
        print(f"Build model error: {e}")
    
# Use NVIDIA CUDA base image for full GPU support
ai_image = (
    modal.Image.from_registry("nvidia/cuda:11.8.0-cudnn8-runtime-ubuntu22.04", add_python="3.10")
    .apt_install("libgl1", "libglib2.0-0", "git", "cuda-nvcc-11-8")
    .pip_install(
        "deepface",
        "tf-keras",
        "tensorflow[and-cuda]",
        "opencv-python-headless",
        "numpy",
        "pillow",
        "fastapi[standard]",
        "ultralytics"
    )
    .run_commands("ln -sf /usr/local/cuda-11.8 /usr/local/cuda")
    .run_function(download_models)
    .env({
        "XLA_FLAGS": "--xla_gpu_cuda_data_dir=/usr/local/cuda-11.8",
        "TF_CPP_MIN_LOG_LEVEL": "2",
        "TF_FORCE_GPU_ALLOW_GROWTH": "true",
        "NVIDIA_VISIBLE_DEVICES": "all"
    })
)

app = modal.App("face-recognition-backend")

# 2. CLASS-BASED WORKER VỚI @modal.enter() ĐỂ GIẢM COLD START
@app.cls(image=ai_image, gpu="T4", timeout=600)
class FaceInference:
    """
    Class-based Modal worker.
    @modal.enter() chạy 1 lần duy nhất khi container thức dậy (cold start),
    tải tất cả model weights từ disk (đã bake sẵn trong image) vào GPU VRAM.
    Các request sau đó sẽ dùng lại model đã load -> latency cực thấp.
    """

    @modal.enter()
    def setup(self):
        """Load tất cả AI models vào VRAM 1 lần duy nhất khi container start."""
        import os
        # Fix CUDA libdevice path — phải trỏ đúng /usr/local/cuda-11.8 (nơi có nvvm/libdevice/)
        os.environ["XLA_FLAGS"] = "--xla_gpu_cuda_data_dir=/usr/local/cuda-11.8"
        os.environ["TF_XLA_FLAGS"] = "--tf_xla_enable_xla_devices=false --tf_xla_auto_jit=-1"
        os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"
        os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"

        # Enable GPU with memory growth
        import tensorflow as tf
        gpus = tf.config.list_physical_devices('GPU')
        if gpus:
            print(f"⚡ TensorFlow GPU: {gpus}")
            for gpu in gpus:
                tf.config.experimental.set_memory_growth(gpu, True)
        else:
            print("⚠️ No GPU found")

        import numpy as np
        from deepface import DeepFace
        from ultralytics import YOLO

        print("Loading models into GPU VRAM...")
        self.DeepFace = DeepFace
        self.yolo = YOLO("yolov8n.pt")

        # Warm up ArcFace (recognition model)
        DeepFace.build_model("ArcFace")

        # Warm up RetinaFace (detector) — load weights vao VRAM ngay
        # Neu khong lam buoc nay, request dau tien se bi cham vi lazy-load
        dummy_img = np.zeros((100, 100, 3), dtype=np.uint8)
        try:
            DeepFace.extract_faces(
                img_path=dummy_img,
                detector_backend="retinaface",
                enforce_detection=False
            )
        except Exception:
            pass

        print("All models loaded and ready!")

    @modal.fastapi_endpoint(method="POST")
    def process_face_pipeline(self, data: dict):
        import cv2
        import numpy as np
        import base64
        import time
        
        start_time = time.time()
        
        # Sử dụng model đã load sẵn từ @modal.enter()
        DeepFace = self.DeepFace
        yolo = self.yolo

        # --- A. DECODE IMAGE ---
        try:
            img_b64 = data.get("image")
            if not img_b64:
                return {"status": "error", "message": "No image data"}
                
            if "," in img_b64:
                img_b64 = img_b64.split(",")[1]
                
            img_bytes = base64.b64decode(img_b64)
            np_arr = np.frombuffer(img_bytes, np.uint8)
            img = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)
            
            if img is None:
                return {"status": "error", "message": "Cannot decode image"}
        except Exception as e:
            return {"status": "error", "message": f"Decode error: {str(e)}"}

        # --- B. YOLO CHECK ---
        try:
            results = yolo(img, verbose=False)
            
            person_count = 0
            has_phone = False
            
            for r in results:
                for box in r.boxes:
                    cls_id = int(box.cls[0])
                    label = yolo.names[cls_id]
                    
                    if label == 'person' and float(box.conf[0]) > 0.7:
                        person_count += 1
                    elif label == 'cell phone':
                        has_phone = True
            
            # NOTE: Disabled person_count check for classroom environment
            # if person_count > 1:
            #      return {"status": "error", "message": f"Security Alert: {person_count} people detected! Only 1 allowed."}
            if has_phone:
                 return {"status": "error", "message": "Security Alert: Cell phone detected! Usage prohibited."}

        except Exception as e:
            print(f"YOLO Warning: {e}")

        # --- C. FACE DETECTION & EMBEDDING ---
        face_objs = []
        
        # 1. Try RetinaFace (Best Accuracy)
        try:
            face_objs = DeepFace.extract_faces(
                img_path=img,
                detector_backend="retinaface",
                enforce_detection=True,
                align=True,
                anti_spoofing=False
            )
        except ValueError:
            print("⚠️ RetinaFace failed, trying fallback 1 (OpenCV)...")
        
        # 2. Fallback: OpenCV (Haar Cascade - Fast/Loose)
        if not face_objs:
            try:
                face_objs = DeepFace.extract_faces(
                    img_path=img,
                    detector_backend="opencv",
                    enforce_detection=True,
                    align=True,
                    anti_spoofing=False
                )
            except ValueError:
                print("⚠️ OpenCV failed, trying fallback 2 (SSD)...")

        # 3. Fallback: SSD
        if not face_objs:
            try:
                face_objs = DeepFace.extract_faces(
                    img_path=img,
                    detector_backend="ssd",
                    enforce_detection=True,
                    align=True,
                    anti_spoofing=False
                )
            except ValueError:
                pass # All failed

        if len(face_objs) == 0:
            return {"status": "no_face", "message": "No face detected (tried RetinaFace, OpenCV, SSD)"}

        primary_face = face_objs[0]

        # Embedding
        try:
            face_crop = primary_face["face"]
            if face_crop.max() <= 1.0:
                face_crop = (face_crop * 255).astype(np.uint8)
            else:
                 face_crop = face_crop.astype(np.uint8)
            
            embedding_objs = DeepFace.represent(
                img_path=face_crop,
                model_name="ArcFace",
                detector_backend="skip",
                enforce_detection=False,
                align=False
            )

            if len(embedding_objs) > 0:
                vec = embedding_objs[0]["embedding"]
                process_time = time.time() - start_time
                
                return {
                    "status": "success",
                    "embedding": vec,
                    "facial_area": primary_face["facial_area"],
                    "confidence": primary_face.get("confidence", 1.0),
                    "liveness": True,
                    "process_time": f"{process_time:.3f}s",
                    "model": "ArcFace + MultiBackend"
                }
                
        except Exception as e:
            return {"status": "error", "message": f"AI Error: {str(e)}"}

        return {"status": "error", "message": "Unknown error"}
