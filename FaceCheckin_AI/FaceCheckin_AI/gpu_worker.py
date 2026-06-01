import modal

# 1. ĐỊNH NGHĨA MÔI TRƯỜNG (IMAGE)
def download_models():
    import os
    from deepface import DeepFace
    from ultralytics import YOLO
    
    os.makedirs("/root/.deepface/weights", exist_ok=True)
    
    print("⏳ Downloading ArcFace, RetinaFace, AntiSpoofing & YOLOv8...")
    try:
        # 1. Face Recognition Models
        DeepFace.build_model("ArcFace")
        
        # 2. YOLO
        YOLO("yolov8n.pt") 
        
    except Exception as e:
        print(f"Build model error: {e}")
    
ai_image = (
    modal.Image.debian_slim()
    .apt_install("libgl1", "libglib2.0-0", "git")
    .pip_install(
        "deepface",
        "tf-keras",
        "tensorflow",
        "opencv-python-headless",
        "numpy",
        "pillow",
        "gfpgan",
        "basicsr",
        "fastapi[standard]",
        "ultralytics" # <--- YOLOv8
    )
    .run_commands(
        # Fix basicsr compatibility with newer torchvision
        "find /usr/local/lib/python3.10/site-packages/basicsr -name '*.py' -exec sed -i 's/torchvision.transforms.functional_tensor/torchvision.transforms.functional/g' {} +",
        "find /usr/local/lib/python3.10/site-packages/basicsr -name '*.pyc' -delete"
    )
    .run_function(download_models)
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
        # Disable XLA/JIT to prevent T4 GPU crash
        os.environ["TF_XLA_FLAGS"] = "--tf_xla_enable_xla_devices=false"
        os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"

        from deepface import DeepFace
        from ultralytics import YOLO

        print("🚀 Loading models into GPU VRAM...")
        self.DeepFace = DeepFace
        self.yolo = YOLO("yolov8n.pt")

        # Warm up ArcFace model (force load weights into memory)
        # RetinaFace is a detector backend, loaded automatically by extract_faces()
        DeepFace.build_model("ArcFace")
        print("✅ All models loaded and ready!")

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

        # Graceful degradation for GFPGAN
        GFPGANer = None

        # --- A. GIẢI MÃ ẢNH ---
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

        # --- B. LÀM NÉT ẢNH (GFPGAN) - Chạy trước để AI nhìn rõ hơn ---
        enable_restore = data.get("restore", False)
        if enable_restore and GFPGANer:
            try:
                restorer = GFPGANer(
                    model_path='https://github.com/TencentARC/GFPGAN/releases/download/v1.3.0/GFPGANv1.3.0.pth',
                    upscale=1,
                    arch='clean',
                    channel_multiplier=2,
                    bg_upsampler=None
                )
                _, _, img = restorer.enhance(img, has_aligned=False, only_center_face=False, paste_back=True)
            except Exception as e:
                print(f"Warning: GFPGAN failed: {e}")

        # --- C. VIRTUAL PROCTOR (YOLOv8) ---
        try:
            results = yolo(img, verbose=False)
            
            person_count = 0
            has_phone = False
            
            for r in results:
                for box in r.boxes:
                    cls_id = int(box.cls[0])
                    label = yolo.names[cls_id]
                    
                    if label == 'person':
                        person_count += 1
                    elif label == 'cell phone':
                        has_phone = True
            
            if person_count > 1:
                 return {"status": "error", "message": f"Security Alert: {person_count} people detected! Only 1 allowed."}
            if has_phone:
                 return {"status": "error", "message": "Security Alert: Cell phone detected! usage prohibited."}

        except Exception as e:
            print(f"YOLO Warning: {e}")

        # --- D. NHẬN DIỆN & ANTI-SPOOFING (ArcFace + FAS) ---
        try:
            enable_antispoof = data.get("anti_spoof", False)
            
            face_objs = DeepFace.extract_faces(
                img_path=img,
                detector_backend="retinaface",
                enforce_detection=True,
                align=True,
                anti_spoofing=enable_antispoof
            )
            
            if len(face_objs) == 0:
                 return {"status": "no_face", "message": "No face detected"}

            primary_face = face_objs[0]
            
            if enable_antispoof:
                is_real = primary_face.get("is_real", True)
                trust_score = primary_face.get("antispoof_score", 0.0)
                
                if not is_real:
                     return {
                         "status": "spoofing", 
                         "message": "Security Alert: Fake face detected (Screen/Paper)!",
                         "score": trust_score
                     }

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
                    "model": "ArcFace + RetinaFace + MiniFASNet"
                }
                
        except ValueError:
            return {"status": "no_face", "message": "No face detected"}
        except Exception as e:
            return {"status": "error", "message": f"AI Error: {str(e)}"}

        return {"status": "error", "message": "Unknown error"}
