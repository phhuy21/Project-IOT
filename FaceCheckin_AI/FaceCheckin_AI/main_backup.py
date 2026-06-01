import os
import time
import numpy as np
import traceback
from datetime import datetime, timezone, timedelta
import base64
import io
import threading
from functools import wraps
import smtplib
from email.mime.text import MIMEText
import random
import uuid
from urllib.parse import urlencode
import urllib.parse
import requests
import json
import google.auth
from google.auth import impersonated_credentials

os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"

# Import thư viện AI/DB
import faiss
from concurrent.futures import ThreadPoolExecutor, as_completed
from flask import Flask, request, jsonify
from flask_cors import CORS
import firebase_admin
from firebase_admin import credentials, firestore, storage
# from deepface import DeepFace # (OFFLOADED TO MODAL)
# from PIL import Image # (Ít dùng but giữ nếu cần)

# --- CẤU HÌNH MODAL AI (Thêm mới) ---
# ⚠️ DÁN LINK BẠN VỪA COPY VÀO DƯỚI ĐÂY
MODAL_API_URL = "https://phhuy21--face-recognition-backend-process-face-pipeline.modal.run"

def call_modal_ai(image_base64, enable_restore=False):
    """
    Hàm này gửi ảnh sang Modal để xử lý (Làm nét + Tìm mặt + Nhận diện).
    Thay thế hoàn toàn việc chạy DeepFace trên Cloud Run.
    """
    try:
        payload = {
            "image": image_base64,
            "restore": enable_restore # True nếu muốn dùng GFPGAN làm nét
        }
        # Gửi request, chờ tối đa 60s (để Modal kịp khởi động nếu đang ngủ)
        response = requests.post(MODAL_API_URL, json=payload, timeout=60)
        
        if response.status_code == 200:
            result = response.json()
            if result.get("status") == "success":
                # Trả về dữ liệu chuẩn để code cũ dùng được ngay
                return {
                    "embedding": result["embedding"],
                    "facial_area": result["facial_area"],
                    "liveness": result.get("liveness", True),
                    "process_time": result.get("process_time")
                }
            else:
                print(f"⚠️ Modal AI trả về lỗi: {result.get('message')}")
                return None
        else:
            print(f"❌ Lỗi kết nối Modal: {response.status_code}")
            return None
            
    except Exception as e:
        print(f"❌ Lỗi gọi Modal: {str(e)}")
        return None


# --- CẤU HÌNH (ENV) ---
# Tên Model & Backend (Cố định để đồng bộ register/verify)
MODEL_NAME = os.getenv("MODEL_NAME", "ArcFace")
DETECTOR_BACKEND = os.getenv("DETECTOR_BACKEND", "retinaface")  # For registration (accurate)
VERIFY_DETECTOR_BACKEND = os.getenv("VERIFY_DETECTOR_BACKEND", "yunet")  # For verify (fast)
MAX_VERIFY_FRAMES = int(os.getenv("MAX_VERIFY_FRAMES", "3"))  # Limit frames for faster verify

# Cấu hình FAISS & Search
# "cosine" hoặc "l2"
FAISS_METRIC = os.getenv("FAISS_METRIC", "cosine").lower()
# Có normalize vector trước khi search/store không? 
# "1", "true", "yes" -> True. Với Cosine thì BẮT BUỘC nên là True.
NORMALIZE_ENV = os.getenv("NORMALIZE_EMBEDDINGS", "1").lower()
NORMALIZE_EMBEDDINGS = NORMALIZE_ENV in ("1", "true", "yes")

# Ngưỡng logic Matching (Mặc định cho Cosine - tối ưu cho lớp 50-100 người)
COSINE_MIN = float(os.getenv("COSINE_MIN", "0.55"))
COSINE_GAP_MIN = float(os.getenv("COSINE_GAP_MIN", "0.03"))
COSINE_RATIO_MAX = float(os.getenv("COSINE_RATIO_MAX", "0.97"))

# Warmup & Key
WARMUP_ON_START = os.getenv("WARMUP_ON_START", "1").lower() in ("1", "true", "yes")
API_KEY = os.getenv("API_KEY", "").strip()

# [NEW] Email Config
SMTP_EMAIL = os.getenv("SMTP_EMAIL", "").strip()
SMTP_PASSWORD = os.getenv("SMTP_PASSWORD", "").strip()
SMTP_HOST = os.getenv("SMTP_HOST", "smtp.gmail.com").strip()
SMTP_PORT = int(os.getenv("SMTP_PORT", "465"))

# Firebase Config
BUCKET_NAME = "phamgiahuy-9ab1c.firebasestorage.app" 
CRED_PATH = "serviceAccountKey.json"
COLLECTION_NAME = "FaceVectors"
ATT_LOGS_COLLECTION = "attendance_logs"
VN_TZ = timezone(timedelta(hours=7))

app = Flask(__name__)
CORS(app) # [NEW] Enable CORS for Web App

# --- GLOBAL STATE & LOCK ---
INDEX_LOCK = threading.Lock()
# Structure: { "teacher_id": { "index": faiss_obj, "user_map": {}, "dim": 512 } }
TEACHER_MANAGERS = {}

# Kết nối Firebase
if not firebase_admin._apps:
    try:
        if os.path.exists(CRED_PATH):
            cred = credentials.Certificate(CRED_PATH)
            firebase_admin.initialize_app(cred, {'storageBucket': BUCKET_NAME})
            print("🔥 Firebase initialized with serviceAccountKey.json")
        else:
            print("⚠️ No serviceAccountKey.json, using Application Default Credentials (ADC)...")
            firebase_admin.initialize_app(options={'storageBucket': BUCKET_NAME})
            print("🔥 Firebase initialized with ADC")
    except Exception as e:
        print(f"⚠️ Warning: Không init được Firebase: {e}")

try:
    db = firestore.client()
    bucket = storage.bucket()
except:
    db = None
    bucket = None

# --- MULTI-TENANCY CONFIG (LEVEL 2: teacher/term/class) ---
TEACHERS_COLLECTION = "Teachers"
TERMS_COLLECTION = "terms"
CLASSES_COLLECTION = "classes"

def _norm(v, default=None):
    if v is None:
        return default
    s = str(v).strip()
    return s if s else default

def get_teacher_id_from_req():
    return _norm(request.headers.get("X-Teacher-ID"))

def get_term_id_from_req():
    return _norm(request.headers.get("X-Term-ID"), "default_term")

def get_class_id_from_req():
    return _norm(request.headers.get("X-Class-ID"), "default_class")

def make_context_key(teacher_id, term_id, class_id):
    return f"{teacher_id or 'global'}|{term_id or 'default_term'}|{class_id or 'default_class'}"

def get_class_root(teacher_id, term_id=None, class_id=None):
    if not teacher_id:
        return None
    term_id = _norm(term_id, "default_term")
    class_id = _norm(class_id, "default_class")
    return f"{TEACHERS_COLLECTION}/{teacher_id}/{TERMS_COLLECTION}/{term_id}/{CLASSES_COLLECTION}/{class_id}"

def get_face_collection(teacher_id, term_id=None, class_id=None):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/FaceVectors"
    # fallback legacy
    if teacher_id:
        return f"{TEACHERS_COLLECTION}/{teacher_id}/FaceVectors"
    return "FaceVectors"

def get_roster_collection(teacher_id, term_id=None, class_id=None):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/roster"
    if teacher_id:
        return f"{TEACHERS_COLLECTION}/{teacher_id}/roster"
    return "roster"

def get_log_collection(teacher_id, term_id=None, class_id=None):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/attendance_logs"
    # fallback legacy
    if teacher_id:
        return f"{TEACHERS_COLLECTION}/{teacher_id}/attendance_logs"
    return "attendance_logs"

def get_photo_prefix(teacher_id, term_id, class_id, name, mssv):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/photos/{name}_{mssv}/"
    if teacher_id:
        return f"{TEACHERS_COLLECTION}/{teacher_id}/photos/{name}_{mssv}/"
    return f"photos/{name}_{mssv}/"

# --- ROUTES ---
@app.route('/', methods=['GET'])
def health_check():
    return jsonify({
        "status": "ok",
        "message": "Face API is running!",
        "timestamp": int(time.time())
    }), 200

# --- AUTH ROUTES ---
# --- EMAIL HELPER ---
def send_email_otp(to_email, otp):
    if not SMTP_EMAIL or not SMTP_PASSWORD:
        print("⚠️ SMTP credentials not set. Skipping email.")
        return False

    try:
        msg = MIMEText(f"Mã xác nhận của bạn là: {otp}\nMã có hiệu lực trong 5 phút.")
        msg['Subject'] = "FaceCheckin - Mã xác nhận quên mật khẩu"
        msg['From'] = SMTP_EMAIL
        msg['To'] = to_email

        with smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT) as server:
            server.login(SMTP_EMAIL, SMTP_PASSWORD)
            server.sendmail(SMTP_EMAIL, to_email, msg.as_string())
        return True
    except Exception as e:
        print(f"Error sending email: {e}")
        return False

@app.route('/auth/register', methods=['POST'])
def auth_register():
    try:
        data = request.json or {}
        username = data.get("username", "").strip()
        password = data.get("password", "").strip()
        email = data.get("email", "").strip() # [NEW]
        
        if not username or not password:
            return jsonify({"error": "Missing info"}), 400
            
        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        if doc_ref.get().exists:
            return jsonify({"error": "Account exists"}), 409
            
        doc_ref.set({
            "username": username,
            "password": password, 
            "email": email, # [NEW]
            "created_at": firestore.SERVER_TIMESTAMP
        })
        return jsonify({"status": "success", "message": f"Welcome {username}"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/auth/login', methods=['POST'])
def auth_login():
    try:
        data = request.json or {}
        username = data.get("username", "").strip()
        password = data.get("password", "").strip()
        
        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        snap = doc_ref.get()
        if not snap.exists:
             return jsonify({"error": "User not found"}), 404
             
        user_data = snap.to_dict()
        if user_data.get("password") == password:
             return jsonify({
                 "status": "success", 
                 "teacher_id": username,
                 "message": "Login successful"
             }), 200
        else:
             return jsonify({"error": "Wrong password"}), 401
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/auth/forgot-password', methods=['POST'])
def forgot_password():
    data = request.json
    email = data.get('email')
    
    # Check if email exists
    users = db.collection(TEACHERS_COLLECTION).where('email', '==', email).stream()
    user_doc = None
    for u in users:
        user_doc = u
        break
        
    if not user_doc:
        return jsonify({"status": "error", "message": "Email không tồn tại!"}), 404
        
    otp = str(random.randint(100000, 999999))
    
    db.collection('PasswordResets').document(email).set({
        "otp": otp,
        "expires_at": int(time.time()) + 300
    })
    
    if send_email_otp(email, otp):
        return jsonify({"status": "success", "message": "OTP sent"})
    else:
        return jsonify({"status": "error", "message": "Failed to send email"}), 500

@app.route('/auth/reset-password', methods=['POST'])
def reset_password():
    data = request.json
    email = data.get('email')
    otp = data.get('otp')
    new_password = data.get('new_password')
    
    reset_doc = db.collection('PasswordResets').document(email).get()
    if not reset_doc.exists:
        return jsonify({"status": "error", "message": "Yêu cầu không hợp lệ"}), 400
        
    reset_data = reset_doc.to_dict()
    if reset_data['otp'] != otp:
        return jsonify({"status": "error", "message": "Mã OTP sai"}), 400
        
    if int(time.time()) > reset_data['expires_at']:
        return jsonify({"status": "error", "message": "Mã OTP đã hết hạn"}), 400
        
    users = db.collection(TEACHERS_COLLECTION).where('email', '==', email).stream()
    for u in users:
        u.reference.update({"password": new_password})
        
    db.collection('PasswordResets').document(email).delete()
    
    return jsonify({"status": "success", "message": "Reset done"})

@app.route('/auth/change-password', methods=['POST'])
def change_password():
    try:
        data = request.json
        username = data.get('username')
        old_password = data.get('old_password')
        new_password = data.get('new_password')
        
        if not username or not old_password or not new_password:
             return jsonify({"status": "error", "message": "Thiếu thông tin"}), 400

        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        snap = doc_ref.get()
        
        if not snap.exists:
            return jsonify({"status": "error", "message": "Tài khoản không tồn tại"}), 404
            
        user_data = snap.to_dict()
        if user_data.get("password") != old_password:
            return jsonify({"status": "error", "message": "Mật khẩu cũ không đúng"}), 400
            
        doc_ref.update({"password": new_password})
        return jsonify({"status": "success", "message": "Đổi mật khẩu thành công"}), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500

# --- HELPERS ---

def _ms():
    return int(time.time() * 1000)

def _l2_normalize_np(x: np.ndarray) -> np.ndarray:
    """
    Normalize vector(s) to unit length (L2 norm).
    x shape: (d,) or (n, d)
    """
    if x.ndim == 1:
        norm = np.linalg.norm(x)
        if norm < 1e-7:
            return x
        return x / norm
    else:
        # shape (n, d)
        norm = np.linalg.norm(x, axis=1, keepdims=True)
        # tránh chia 0
        norm[norm < 1e-7] = 1e-7 
        return x / norm

def _clean_base64(b64: str) -> str:
    """Loại bỏ prefix data:image/...;base64, nếu có"""
    if "," in b64:
        return b64.split(",", 1)[1]
    return b64

def _require_api_key(f):
    @wraps(f)
    def decorated(*args, **kwargs):
        # Nếu không set API_KEY env thì coi như không bảo vệ
        if not API_KEY:
            return f(*args, **kwargs)
        
        # Check header
        key_header = request.headers.get("x-api-key") or request.headers.get("X-API-Key")
        if key_header == API_KEY:
            return f(*args, **kwargs)
        return jsonify({"error": "Unauthorized"}), 401
    return decorated

def vn_now():
    dt = datetime.now(VN_TZ)
    return dt, int(dt.timestamp() * 1000), dt.strftime("%Y-%m-%d"), dt.strftime("%H:%M:%S")

@firestore.transactional
def _upsert_attendance(transaction, doc_ref, payload):
    snap = doc_ref.get(transaction=transaction)
    if snap.exists:
        payload.pop("created_at", None)
    transaction.set(doc_ref, payload, merge=True)

def write_attendance_once_per_day(teacher_id, term_id, class_id, name, mssv, distance, match_percent):
    if not mssv or not db:
        return None

    col_path = get_log_collection(teacher_id, term_id, class_id)
    _, epoch_ms, date_str, time_str = vn_now()
    doc_id = f"{date_str}_{mssv}"  # 1 MSSV / 1 Day
    doc_ref = db.collection(col_path).document(doc_id)

    payload = {
        "name": name,
        "mssv": mssv,
        "date": date_str,
        "time": time_str,
        "status": "Present",
        "distance": float(distance),
        "match_percent": float(match_percent),
        "vn_epoch_ms": int(epoch_ms),
        "created_at": firestore.SERVER_TIMESTAMP,
        "updated_at": firestore.SERVER_TIMESTAMP,
        "source": "cloudrun_verify",
    }

    try:
        tx = db.transaction()
        _upsert_attendance(tx, doc_ref, payload)
        return {
            "log_id": doc_id,
            "log_date": date_str,
            "log_time": time_str,
            "log_vn_epoch_ms": epoch_ms
        }
    except Exception as e:
        print(f"Write log error: {e}")
        return None

def calculate_average_embedding(embeddings_list):
    """
    Hàm tính vector trung bình từ danh sách các vector khuôn mặt.
    Thực hiện: Cộng trung bình -> Chuẩn hóa L2.
    """
    if not embeddings_list:
        return None

    try:
        # 1. Chuyển list thành numpy array
        matrix = np.array(embeddings_list)
        
        # 2. Tính trung bình cộng dọc (Mean)
        mean_vector = np.mean(matrix, axis=0)
        
        # 3. Chuẩn hóa L2
        norm = np.linalg.norm(mean_vector)
        if norm == 0:
            return mean_vector.tolist()
            
        final_vector = (mean_vector / norm).tolist()
        return final_vector
        
    except Exception as e:
        print(f"❌ Lỗi tính vector trung bình: {e}")
        return None

# --- CORE: LOAD TEACHER DATA (CONTEXT-BASED) ---
def load_teacher_data(teacher_id, term_id="default_term", class_id="default_class"):
    """
    Load data specific to (teacher, term, class) into memory.
    """
    global TEACHER_MANAGERS
    
    ctx_key = make_context_key(teacher_id, term_id, class_id)

    with INDEX_LOCK:
        try:
            col_path = get_face_collection(teacher_id, term_id, class_id)
            print(f"⏳ Loading data for [{ctx_key}] from {col_path}...")
            
            if not db:
                print("❌ No DB Connection")
                TEACHER_MANAGERS[ctx_key] = {"index": None, "user_map": {}, "dim": None}
                return 

            docs = db.collection(col_path).stream()

            temp_vectors = []
            temp_users = []
            count = 0
            dim = None

            for doc in docs:
                data = doc.to_dict()
                
                # [FIX] Load ALL vectors (Supports both 'vectors' list and new 'embedding' field)
                vecs = data.get("vectors")
                single_embedding = data.get("embedding")
                
                entries = []
                
                # Priority 1: New single averaged embedding
                if single_embedding and isinstance(single_embedding, list):
                    entries = [single_embedding]
                
                # Priority 2: Legacy list of vectors
                elif vecs:
                    if isinstance(vecs, list):
                        if len(vecs) > 0 and isinstance(vecs[0], (float, int)):
                            entries = [vecs]       # legacy: list float = 1 vector
                        else:
                            entries = vecs         # list dict hoặc list list
                    elif isinstance(vecs, dict):
                        entries = [vecs]
                
                if not entries:
                    continue

                for raw_entry in entries:
                    final_vector = []

                    # Support multiple formats
                    if isinstance(raw_entry, dict) and "embedding" in raw_entry:
                        final_vector = raw_entry["embedding"]
                    elif isinstance(raw_entry, dict) and "data" in raw_entry:
                        final_vector = raw_entry["data"]
                    elif isinstance(raw_entry, list):
                        final_vector = raw_entry
                    else:
                        continue

                    if not isinstance(final_vector, list) or len(final_vector) == 0:
                        continue

                    if dim is None:
                        dim = len(final_vector)

                    if len(final_vector) != dim:
                        print(f"⚠️ Bỏ qua doc {doc.id}: vector_len={len(final_vector)} != dim={dim}")
                        continue

                    temp_vectors.append(final_vector)
                    temp_users.append({
                        "id": doc.id,
                        "name": data.get("name", "Unknown"),
                        "mssv": data.get("mssv", doc.id)
                    })
                    count += 1

            manager = {
                "index": None,
                "user_map": {},
                "dim": None
            }

            if temp_vectors:
                vectors_np = np.array(temp_vectors, dtype="float32")
                
                if NORMALIZE_EMBEDDINGS:
                    vectors_np = _l2_normalize_np(vectors_np)

                if FAISS_METRIC == "cosine":
                    new_index = faiss.IndexFlatIP(dim)
                    print(f"ℹ️ FAISS Index: Cosine/InnerProduct (dim={dim})")
                else:
                    new_index = faiss.IndexFlatL2(dim)
                    print(f"ℹ️ FAISS Index: L2 Distance (dim={dim})")

                new_index.add(vectors_np)

                manager["index"] = new_index
                manager["user_map"] = {i: temp_users[i] for i in range(len(temp_users))}
                manager["dim"] = dim

                print(f"✅ LOADED [{ctx_key}]: {count} faces. Metric={FAISS_METRIC}")
            else:
                print(f"⚠️ [{ctx_key}] has no face data (Empty Collection).")

            TEACHER_MANAGERS[ctx_key] = manager

        except Exception as e:
            print(f"❌ LỖI LOAD DATA CHO Context={ctx_key}: {e}")
            traceback.print_exc()

# [NEW] Continuous Learning Helper
def auto_learn_face(teacher_id, term_id, class_id, mssv, vector):
    """
    Background task to append new vector to student profile.
    Triggered when High Confidence match occurs.
    """
    try:
        if not mssv or mssv == "Unknown" or not db:
            return
            
        col_path = get_face_collection(teacher_id, term_id, class_id)
        doc_ref = db.collection(col_path).document(mssv)
        
        # 1. Get current vectors
        snap = doc_ref.get()
        if not snap.exists:
            return 
            
        data = snap.to_dict()
        old_vectors = data.get("vectors", [])
        
        # Standardize
        if isinstance(old_vectors, list) and len(old_vectors) > 0 and isinstance(old_vectors[0], (float, int)):
             old_vectors = [{"embedding": old_vectors, "dim": len(old_vectors)}]
        elif not isinstance(old_vectors, list):
             old_vectors = []
        
        # Append
        new_entry = {
            "embedding": vector,
            "dim": len(vector),
            "created_at": datetime.now(timezone.utc),
            "source": "auto_learn",
            "model": MODEL_NAME
        }
        old_vectors.append(new_entry)
        
        # Limit FIFO (Keep max 15)
        if len(old_vectors) > 15:
            old_vectors = old_vectors[-15:]
            
        doc_ref.set({"vectors": old_vectors, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
        print(f"🧠 Auto-learned for {mssv}. Total: {len(old_vectors)}")
        
    except Exception as e:
        print(f"❌ Auto-learn failed for {mssv}: {e}")

# --- ROUTES ---

@app.route('/verify', methods=['POST'])
def check_in():
    print("DEBUG: Request verify received!")
    verify_path = None
    verify_paths = []
    
    t_backend0 = _ms()
    t_embed = 0.0
    t_match = 0.0   # ✅ NEW: lưu nhiều file tmp khi multi-frame
    try:
        data = request.json
        # [NEW] Get context (teacher/term/class)
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        ctx_key = make_context_key(teacher_id, term_id, class_id)

        if not data:
             return jsonify({"error": "No JSON data"}), 400

        dfs = []
        
        # 1. Processing Image (UPGRADED: support multi-frame)
        if ('imgs_base64' in data) or ('images' in data):
            b64_list = data.get('imgs_base64') or data.get('images') or []
            b64_list = b64_list[:MAX_VERIFY_FRAMES]  # [FIX] Limit frames for faster verify
            if (not isinstance(b64_list, list)) or (len(b64_list) == 0):
                return jsonify({"error": "imgs_base64/images must be a non-empty list"}), 400

            dfs = []
            for i, b64_item in enumerate(b64_list):
                try:
                    b64_str = _clean_base64(b64_item)
                    # Giữ lại việc lưu file tmp nếu cần debug
                    img_bytes = base64.b64decode(b64_str)
                    tmp_name = f"verify_{time.time()}_{i}.jpg"
                    tmp_path = f"/tmp/{tmp_name}"
                    verify_paths.append(tmp_path)
                    
                    with open(tmp_path, "wb") as f:
                         f.write(img_bytes)

                    # --- MODAL AI CALL ---
                    print(f"🚀 Sending frame {i} to Modal...")
                    modal_result = call_modal_ai(b64_str, enable_restore=True)
                    
                    if modal_result:
                        print(f"✅ Modal done: {modal_result['process_time']}")
                        dfs.append(modal_result)
                    else:
                        print(f"⚠️ Modal failed on frame {i}")

                except Exception as e:
                    print(f"[verify][multi] frame {i} error: {e}")
                    continue

        elif 'img_base64' in data:
            b64_str = _clean_base64(data['img_base64'])
            img_bytes = base64.b64decode(b64_str)

            verify_filename = f"verify_{time.time()}.jpg"
            verify_path = f"/tmp/{verify_filename}"
            verify_paths.append(verify_path)

            with open(verify_path, "wb") as f:
                f.write(img_bytes)

            # --- MODAL AI CALL ---
            print(f"🚀 Sending single image to Modal...")
            modal_result = call_modal_ai(b64_str, enable_restore=True)
            if modal_result:
                 dfs = [modal_result]
            else:
                 dfs = []


        # Legacy filename support
        elif 'filename' in data:
            local_path = f"/tmp/temp_legacy_{time.time()}.jpg"
            verify_paths.append(local_path)
            try:
                blob = bucket.blob(data['filename'])
                if not blob.exists():
                    return jsonify({"error": f"File not found in storage: {data['filename']}"}), 404
                blob.download_to_filename(local_path)

                # Read back as base64 for Modal
                with open(local_path, "rb") as f:
                    b64_str = base64.b64encode(f.read()).decode('utf-8')

                modal_result = call_modal_ai(b64_str, enable_restore=True)
                if modal_result:
                     dfs = [modal_result]
                else:
                     dfs = []

            except Exception as e:
                print(f"Legacy filename error: {e}")
                dfs = []
        else:
            return jsonify({"error": "Missing 'img_base64' or 'imgs_base64/images' or 'filename'"}), 400

        # Fallback removed or simplified? 
        # If Modal fails, we might not have DeepFace to fallback on if we removed the import.
        # But if we keep DeepFace locally for registration, we might still have it.
        # User said "Comment out deepface import", so we can't use DeepFace locally.
        # So fallback should just fail gracefully.


        # 2. No Face
        if not dfs:
            return jsonify({
                "status": "no_face",
                "message": "No face",
                "distance": 999999,
                "match_percent": 0.0
            }), 200

        if ctx_key not in TEACHER_MANAGERS:
             load_teacher_data(teacher_id, term_id, class_id)
        
        manager = TEACHER_MANAGERS.get(ctx_key)
        
        # [NEW] Context-Aware Check
        # Ensure we are using the correct subset of data
        if manager and class_id and class_id != "default_class":
             # Optional: If your logic supports sub-filtering, add it here.
             # Currently, the architecture separates data by `ctx_key` (which includes class_id).
             # So this is already optimized IF client sends class_id.
             pass

        faiss_index = manager["index"] if manager else None
        user_map = manager["user_map"] if manager else {}
        
        # 3. Logic Matching
        best_name = "Unknown"
        best_mssv = "Unknown"
        min_dist = float("inf")
        second_dist = float("inf")
        gap = 0.0
        ratio = 0.0
        status = "not_recognized"
        message = "No match found"
        match_percent = 0.0
        match_percent_raw = 0.0
        log_meta = None

        # [NEW] Quality Filter (MagFace Style Check)
        high_quality_dfs = []
        for d in dfs:
            conf = d.get("confidence", 1.0) # Default to 1.0 if not supported yet
            if conf >= 0.90:
                high_quality_dfs.append(d)
            else:
                print(f"⚠️ Rejected low quality frame: {conf:.2f}")

        if len(dfs) > 0 and len(high_quality_dfs) == 0:
             return jsonify({
                "status": "quality_failed",
                "message": "Image too blurry or bad angle. Please look straight.",
                "distance": 999999,
                "match_percent": 0.0
            }), 200
            
        dfs = high_quality_dfs # Proceed with only good frames

        if len(dfs) > 0:
            vecs = [d.get("embedding") for d in dfs if isinstance(d, dict) and d.get("embedding") is not None]
            if len(vecs) == 0:
                return jsonify({
                    "status": "no_face",
                    "message": "No face",
                    "distance": 999999,
                    "match_percent": 0.0
                }), 200

            if len(vecs) == 1:
                target_vec = vecs[0]
            else:
                # ✅ NEW: normalize từng vector -> average -> normalize lại
                vec_np = np.array(vecs, dtype="float32")
                if NORMALIZE_EMBEDDINGS:
                    vec_np = _l2_normalize_np(vec_np)

                avg = np.mean(vec_np, axis=0)
                if NORMALIZE_EMBEDDINGS:
                    avg = avg / (np.linalg.norm(avg) + 1e-12)

                target_vec = avg.tolist()
            
            # Check dimension mismatch
            current_dim = faiss_index.d if faiss_index else 0
            if len(target_vec) != current_dim:
                print(f"❌ Dimension Mismatch: Input={len(target_vec)}, Index={current_dim}")
                # Auto reload if dimension changed or empty
                if current_dim == 0:
                     load_teacher_data(teacher_id, term_id, class_id) # Retry load
                     manager = TEACHER_MANAGERS.get(ctx_key)
                     faiss_index = manager["index"] if manager else None
                     current_dim = faiss_index.d if faiss_index else 0
                
                if len(target_vec) != current_dim:
                    return jsonify({
                        "status": "not_recognized",
                        "message": f"Embedding dimension mismatch ({len(target_vec)} vs {current_dim})",
                        "distance": 999999
                    }), 200

            if faiss_index and faiss_index.ntotal > 0:
                target_np = np.array(target_vec, dtype="float32").reshape(1, -1)
                
                if NORMALIZE_EMBEDDINGS:
                    target_np = _l2_normalize_np(target_np)

                # ===== ID-AWARE SEARCH: group by MSSV (tránh top-2 cùng 1 người) =====
                ntotal = int(faiss_index.ntotal) if faiss_index else 0
                if (not faiss_index) or ntotal <= 0:
                    resp = {
                        "name": "Unknown",
                        "mssv": "Unknown",
                        "distance": 999.0,
                        "second_distance": None,
                        "gap": 0.0,
                        "ratio": 0.0,
                        "match_percent": 0.0,
                        "match_percent_raw": 0.0,
                        "status": "not_recognized",
                        "message": "Index empty"
                    }
                    return jsonify(resp), 200

                search_k = min(20, ntotal)  # có thể tăng 30 nếu muốn
                
                t0_match = _ms()
                D, I = faiss_index.search(target_np, k=search_k)
                t_match += (_ms() - t0_match)

                best_by_mssv = {}  # mssv -> (val, idx)
                for r in range(search_k):
                    idx = int(I[0][r])
                    if idx < 0:
                        continue
                    info = user_map.get(idx)
                    if not info:
                        continue

                    mssv_key = str(info.get("mssv", "")).strip()
                    if not mssv_key:
                        continue

                    val = float(D[0][r])

                    if FAISS_METRIC == "cosine":
                        # cosine: val càng lớn càng tốt
                        if (mssv_key not in best_by_mssv) or (val > best_by_mssv[mssv_key][0]):
                            best_by_mssv[mssv_key] = (val, idx)
                    else:
                        # L2: val (squared distance) càng nhỏ càng tốt
                        if (mssv_key not in best_by_mssv) or (val < best_by_mssv[mssv_key][0]):
                            best_by_mssv[mssv_key] = (val, idx)

                uniq = sorted(best_by_mssv.values(), key=lambda t: t[0], reverse=(FAISS_METRIC == "cosine"))

                if len(uniq) == 0:
                    resp = {
                        "name": "Unknown",
                        "mssv": "Unknown",
                        "distance": 999.0,
                        "second_distance": None,
                        "gap": 0.0,
                        "ratio": 0.0,
                        "match_percent": 0.0,
                        "match_percent_raw": 0.0,
                        "status": "not_recognized",
                        "message": "No candidates"
                    }
                    return jsonify(resp), 200

                found_id = int(uniq[0][1])
                val_1 = float(uniq[0][0])

                if len(uniq) >= 2:
                    val_2 = float(uniq[1][0])
                    k = 2
                else:
                    k = 1
                    val_2 = -1.0 if FAISS_METRIC == "cosine" else float("inf")
                
                is_match = False

                # --- NHÁNH COSINE ---
                if FAISS_METRIC == "cosine":
                    best_sim = val_1
                    second_sim = val_2 if k == 2 else -1.0
                    
                    gap_sim = (best_sim - second_sim) if k == 2 else 1.0
                    ratio_sim = (second_sim / best_sim) if (k == 2 and best_sim > 1e-6) else 0.0
                    
                    # [SIMPLIFIED] Match logic
                    if k == 1:
                        is_match = (best_sim >= COSINE_MIN)
                    else:
                        is_match = (best_sim >= COSINE_MIN) and (gap_sim >= COSINE_GAP_MIN) and (ratio_sim <= COSINE_RATIO_MAX)

                    # Fallback removed - Modal handles all face processing now

                    min_dist = np.sqrt(max(0.0, 2.0 - 2.0 * best_sim))
                    second_dist = np.sqrt(max(0.0, 2.0 - 2.0 * second_sim)) if second_sim > -0.5 else float("inf")
                    
                    gap = gap_sim 
                    ratio = ratio_sim

                    scale = 1.0 - COSINE_MIN
                    score_raw = max(0.0, (best_sim - COSINE_MIN) / scale) if scale > 0 else (1.0 if best_sim >= COSINE_MIN else 0.0)
                    
                    match_percent_raw = score_raw * 100.0
                    match_percent = match_percent_raw 
                    
                    print(f"[{ctx_key}][COSINE] Sim={best_sim:.3f}, Gap={gap_sim:.3f}, Ratio={ratio_sim:.3f}, Match={is_match}")

                status = "success" if is_match else "not_recognized"
                message = "Recognized" if is_match else "No match found"
                
                if is_match:
                    info = user_map.get(found_id)
                    if info:
                        best_name = info['name']
                        best_mssv = info['mssv']

                # Ghi Log Attendance [UPDATED]
                # Ghi Log Attendance [UPDATED]
                if is_match:
                    log_meta = write_attendance_once_per_day(teacher_id, term_id, class_id, best_name, best_mssv, min_dist, match_percent_raw)

                    # [NEW] Continuous Learning
                    if match_percent_raw >= 98.0:
                         threading.Thread(target=auto_learn_face, args=(teacher_id, term_id, class_id, best_mssv, target_vec)).start()

        # 4. Trả response
        resp = {
            "name": best_name,
            "mssv": best_mssv,
            "distance": float(min_dist),
            "second_distance": float(second_dist) if second_dist != float("inf") else None,
            "gap": float(gap),
            "ratio": float(ratio),
            "match_percent": round(match_percent, 2),
            "match_percent_raw": round(match_percent_raw, 2),
            "status": status,
            "message": message,
            "latency_ms": {
                "embedding": round(t_embed, 2),
                "matching": round(t_match, 2),
                "total_backend": round(_ms() - t_backend0, 2)
            }
        }
        if log_meta:
            resp.update(log_meta)

        return jsonify(resp), 200

    except Exception as e:
        print("Lỗi verify:", e)
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    
    finally:
        for p in (verify_paths or []):
            try:
                if p and os.path.exists(p):
                    os.remove(p)
            except:
                pass

@app.route('/reload', methods=['GET'])
@_require_api_key
def reload():
    teacher_id = get_teacher_id_from_req()
    term_id = get_term_id_from_req()
    class_id = get_class_id_from_req()
    load_teacher_data(teacher_id, term_id, class_id)
    
    ctx_key = make_context_key(teacher_id, term_id, class_id)
    manager = TEACHER_MANAGERS.get(ctx_key)
    count = manager["index"].ntotal if manager and manager["index"] else 0
    
    return jsonify({
        "message": f"Reloaded for [{ctx_key}]", 
        "count": count,
        "metric": FAISS_METRIC
    })

@app.route('/register', methods=['POST'])
@_require_api_key
def register_face():
    local_path = None  # [FIX] Initialize variable scope
    try:

        data = request.get_json(silent=True)  # [FIX] silent=True prevents exception on non-JSON content-type
        imgs = []

        # [NEW] Support Multipart/Form-Data (Unity WWWForm)
        if data is None:
            # Try parsing form-data
            data = request.form.to_dict()
            
            # Extract images from files
            if 'images' in request.files:
                file_list = request.files.getlist('images')
                for f in file_list:
                    # Convert file -> bytes -> base64
                    try:
                        content = f.read()
                        b64 = base64.b64encode(content).decode('utf-8')
                        imgs.append(b64)
                    except Exception as e:
                        print(f"Error reading file upload: {e}")

            # Normalize 'append_mode' from string in form-data
            if 'append_mode' in data:
                 val = str(data['append_mode']).lower()
                 data['append_mode'] = (val == 'true')

        # Logic for JSON input
        elif 'imgs_base64' in data and isinstance(data['imgs_base64'], list):
            imgs = data['imgs_base64']
        elif 'images' in data and isinstance(data['images'], list):
             imgs = data['images']
        elif 'img_base64' in data:
            imgs = [data['img_base64']]
            
        if not imgs or 'name' not in data:
            return jsonify({"error": "Missing info or images (name required)"}), 400

        # [NEW] Get context (teacher/term/class)
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        
        face_col = get_face_collection(teacher_id, term_id, class_id)
        roster_col = get_roster_collection(teacher_id, term_id, class_id)

        name = data['name']
        mssv = data.get('mssv', 'NoID')
        
        if not mssv or mssv == 'NoID' or not str(mssv).strip():
            print(f"❌ Invalid MSSV received: '{mssv}' from data: {data}")
            return jsonify({"error": "Invalid MSSV (empty or missing)"}), 400
            
        mssv = str(mssv).strip()
        
        # Prepare Firestore Doc
        doc_ref = db.collection(face_col).document(mssv)
        snap = doc_ref.get()
        old_vectors = []
        if snap.exists:
             old_vectors = (snap.to_dict() or {}).get("vectors", []) or []

        # Fix legacy format
        if isinstance(old_vectors, list) and len(old_vectors) > 0 and isinstance(old_vectors[0], (float, int)):
             old_vectors = [{
                 "embedding": old_vectors,
                 "dim": len(old_vectors),
                 "metric": FAISS_METRIC
             }]
        elif not isinstance(old_vectors, list):
             old_vectors = []

        # Process each image
        collected_vectors = []
        success_count = 0
        
        # [NEW] Parallel Processing
        # collected_vectors initialized above
        def process_single_image(idx, img_b64):
            # [FIX] Don't use outer scope local_path to avoid race/redeclare
            th_local_path = None
            try:
                img_str = _clean_base64(img_b64)
                img_bytes = base64.b64decode(img_str)
                filename = f'Reg_{mssv}_{int(time.time())}_{idx}.jpg'
                th_local_path = f'/tmp/{filename}'
                with open(th_local_path, 'wb') as f:
                    f.write(img_bytes)
                if bucket:
                    blob_path = f'{get_photo_prefix(teacher_id, term_id, class_id, name, mssv)}{filename}'
                    blob = bucket.blob(blob_path)
                    blob.upload_from_filename(th_local_path)
                # print(f'🚀 [Thread {idx}] Calling Modal AI...')
                modal_result = call_modal_ai(img_str, enable_restore=True)
                
                if modal_result and 'embedding' in modal_result:
                    return modal_result['embedding']
                return None
            except Exception as ex:
                print(f'❌ Error processing img {idx}: {ex}')
                return None
            finally:
                if th_local_path and os.path.exists(th_local_path):
                    try: os.remove(th_local_path)
                    except: pass

        print(f'⚡ Starting parallel processing for {len(imgs)} images...')
        with ThreadPoolExecutor(max_workers=3) as executor:
            future_to_idx = {executor.submit(process_single_image, idx, img): idx for idx, img in enumerate(imgs)}
            for future in as_completed(future_to_idx):
                idx = future_to_idx[future]
                try:
                    vec = future.result()
                    if vec:
                        collected_vectors.append(vec)
                        success_count += 1
                        print(f'✅ Image {idx} done.')
                except Exception as exc:
                    print(f'❌ Image {idx} exception: {exc}')
        # --- [NEW] AVERAGE EMBEDDINGS ---
        if collected_vectors:
            print(f"🔹 Averaging {len(collected_vectors)} vectors for {mssv}...")
            final_vector = calculate_average_embedding(collected_vectors)
            
            # Wrap in the specific format required by the App/DB schema
            # We overwrite 'vectors' list with just ONE entry containing the averaged vector.
            # This replaces the old logic of appending multiple vectors.
            
            new_entry = {
                "embedding": final_vector,
                "dim": len(final_vector),
                "normalized": NORMALIZE_EMBEDDINGS,
                "model": MODEL_NAME,
                "detector": DETECTOR_BACKEND,
                "metric": FAISS_METRIC,
                "created_at": datetime.now(timezone.utc),
                "storage_path": "" # Optional or use the last one
            }
            # Check if we should APPEND to existing vectors (e.g. Hybrid Registration with Mask)
            # or OVERWRITE (Classic Registration)
            append_mode = data.get('append_mode', False)
            
            if append_mode:
                print(f"🔹 Append Mode: Adding new vector to existing {len(old_vectors)} vectors.")
                old_vectors.append(new_entry)
            else:
                # Overwrite old vectors completely with this new averaged one
                old_vectors = [new_entry] 

        if success_count == 0:
             return jsonify({"error": "No valid faces found in any image"}), 400

        # Limit vectors per profile
        MAX_VECTORS = int(os.getenv("MAX_VECTORS_PER_PROFILE", "5"))
        if MAX_VECTORS > 0 and len(old_vectors) > MAX_VECTORS:
             old_vectors = old_vectors[-MAX_VECTORS:]

        # 4. Save to Firestore
        doc_ref.set({
            "name": name,
            "mssv": mssv,
            "vectors": old_vectors,
            "prefix": get_photo_prefix(teacher_id, term_id, class_id, name, mssv),
            "updated_at": firestore.SERVER_TIMESTAMP,
        }, merge=True)
        
        # 5. Update Roster
        db.collection(roster_col).document(mssv).set({
            "name": name,
            "mssv": mssv,
            "registered": True,
            "updated_at": firestore.SERVER_TIMESTAMP,
        }, merge=True)
        
        # 6. Reload RAM
        load_teacher_data(teacher_id, term_id, class_id)
        
        return jsonify({"status": "success", "message": f"Registered {success_count} vectors for {name}"}), 200

        # Duplicate removed

        
        # 6. Reload RAM (for this context)
        load_teacher_data(teacher_id, term_id, class_id)
        
        return jsonify({"status": "success", "message": f"Registered {name}"}), 200

    except Exception as e:
        print("Lỗi register:", e)
        traceback.print_exc()
        return jsonify({"status": "error", "message": str(e)}), 500
    finally:
        pass # [FIX] No outer local_path to clean, threads handle their own coverage

@app.route("/version", methods=["GET"])
def version():
    return jsonify({"version": os.getenv("APP_VERSION", "dev")}), 200

@app.route('/profiles', methods=['GET'])
def list_profiles():
    # Không bắt buộc API Key để client dễ hiển thị
    groups = {} 
    try:
        # [NEW] Get context (teacher/term/class)
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        
        col_path = get_face_collection(teacher_id, term_id, class_id)

        if not db:
             return jsonify({"profiles": [], "count": 0}), 200

        docs = db.collection(col_path).stream()
        for doc in docs:
            d = doc.to_dict() or {}
            # [FIX] Safe string conversion immediately
            name = str(d.get("name", "Unknown") or "Unknown").strip()
            mssv = str(d.get("mssv", "NoID") or "NoID").strip()
            storage_path = d.get("storage_path", "")

            key = mssv  # [FIX] Group by MSSV only
            if key not in groups:
                prefix = ""
                # Logic prefix ảnh giữ nguyên hoặc update nếu cần
                if storage_path:
                     # Simple logic: take directory of file
                     import os
                     prefix = os.path.dirname(storage_path) + "/"
                else: 
                     prefix = f"photos/{name}_{mssv}/"

                groups[key] = {
                    "profile_key": key,
                    "name": name,
                    "mssv": mssv,
                    "prefix": prefix,
                    "doc_count": 0,
                }
            # [FIX] Count actual vectors, not documents
            vecs = d.get("vectors")
            cnt = 0
            if isinstance(vecs, list):
                if len(vecs) > 0 and isinstance(vecs[0], (float, int)):
                    cnt = 1  # legacy format: single vector as list of floats
                else:
                    cnt = len(vecs)  # new format: list of dicts
            elif vecs is not None:
                cnt = 1

            groups[key]["doc_count"] += cnt

        out = list(groups.values())
        # [FIX] Safe sort (convert to str to avoid int vs str comparison error)
        out.sort(key=lambda x: (str(x.get("mssv", "")).strip(), str(x.get("name", "")).strip()))
        return jsonify({"profiles": out, "count": len(out)}), 200
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500

@app.route('/avatar_url', methods=['GET'])
def avatar_url():
    try:
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        mssv = (request.args.get("mssv") or "").strip()
        name = (request.args.get("name") or "").strip()

        if not bucket or not mssv:
            return jsonify({"status": "error", "message": "Missing mssv or bucket not ready"}), 200

        # Nếu client không gửi name thì thử lấy từ roster
        if not name and db:
            roster_col = get_roster_collection(teacher_id, term_id, class_id)
            snap = db.collection(roster_col).document(mssv).get()
            if snap.exists:
                d = snap.to_dict() or {}
                name = (d.get("name") or "").strip()

        # ✅ 1) Try read exact storage_path from FaceVectors (no need list permission)
        if db:
            face_col = get_face_collection(teacher_id, term_id, class_id)
            snap = db.collection(face_col).document(mssv).get()
            if snap.exists:
                d = snap.to_dict() or {}
                sp = (d.get("storage_path") or "").strip()
                if sp:
                    b = bucket.blob(sp)
                    
                    # [FIX] Use Impersonated Credentials if configured
                    signer_sa = (os.getenv("SIGNER_SA_EMAIL") or "").strip()
                    if signer_sa:
                        try:
                            src_creds, _ = google.auth.default()
                            signing_creds = impersonated_credentials.Credentials(
                                source_credentials=src_creds,
                                target_principal=signer_sa,
                                target_scopes=["https://www.googleapis.com/auth/devstorage.read_only"],
                                lifetime=900,
                            )
                            url = b.generate_signed_url(
                                version="v4",
                                expiration=timedelta(minutes=10),
                                method="GET",
                                credentials=signing_creds,
                            )
                            return jsonify({"status": "success", "url": url}), 200
                        except Exception as e_impersonate:
                            print(f"[avatar_url] Impersonation failed: {e_impersonate}")
                            # Fallback if impersonation fails (though likely to fail if PERMISSION_DENIED was the issue)

                    # Standard signing (works if using Service Account Key, fails on Cloud Run with default SA w/o SignBlob)
                    url = b.generate_signed_url(
                        version="v4",
                        expiration=timedelta(minutes=10),
                        method="GET"
                    )
                    return jsonify({"status": "success", "url": url}), 200

        if not name:
            name = "Unknown"

        prefix = get_photo_prefix(teacher_id, term_id, class_id, name, mssv)

        def _first_blob(pfx):
            for b in bucket.list_blobs(prefix=pfx, max_results=1):
                return b
            return None

        b = _first_blob(prefix)
        if not b and " " in prefix:
            b = _first_blob(prefix.replace(" ", "_"))

        if not b:
            return jsonify({"status": "not_found"}), 200

        # [FIX] Use Impersonated Credentials if configured
        signer_sa = (os.getenv("SIGNER_SA_EMAIL") or "").strip()
        if signer_sa:
             try:
                src_creds, _ = google.auth.default()
                signing_creds = impersonated_credentials.Credentials(
                    source_credentials=src_creds,
                    target_principal=signer_sa,
                    target_scopes=["https://www.googleapis.com/auth/devstorage.read_only"],
                    lifetime=900,
                )
                url = b.generate_signed_url(
                    version="v4",
                    expiration=timedelta(minutes=10),
                    method="GET",
                    credentials=signing_creds,
                )
                return jsonify({"status": "success", "url": url}), 200
             except Exception as e_impersonate:
                 print(f"[avatar_url] Impersonation failed: {e_impersonate}")

        url = b.generate_signed_url(
            version="v4",
            expiration=timedelta(minutes=10),
            method="GET"
        )
        return jsonify({"status": "success", "url": url}), 200

    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 200


@app.route('/correct_identity', methods=['POST'])
@_require_api_key
def correct_identity():
    """
    Feedback Loop Endpoint:
    Allows teacher to manually correct a face.
    1. Recalculate vector for the image.
    2. Add to the correct student's profile (Append mode).
    3. Update FAISS index immediately.
    """
    try:
        data = request.json
        if not data or 'img_base64' not in data or 'correct_mssv' not in data:
            return jsonify({"error": "Missing info"}), 400
            
        img_str = _clean_base64(data['img_base64'])
        correct_mssv = data['correct_mssv']
        student_name = data.get('student_name', 'Unknown')
        
        # Context
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        ctx_key = make_context_key(teacher_id, term_id, class_id)
        
        print(f"🔧 Correcting identity for [{correct_mssv}] in ctx [{ctx_key}]...")

        # 1. Get Vector from Modal
        modal_result = call_modal_ai(img_str, enable_restore=True)
        if not modal_result or "embedding" not in modal_result:
             return jsonify({"error": "Could not extract face for correction"}), 400
             
        new_vec = modal_result["embedding"]
        
        # 2. Update Firestore
        face_col = get_face_collection(teacher_id, term_id, class_id)
        doc_ref = db.collection(face_col).document(correct_mssv)
        
        snap = doc_ref.get()
        old_vectors = []
        if snap.exists:
            old_vectors = (snap.to_dict() or {}).get("vectors", []) or []
            if not student_name or student_name == 'Unknown':
                 student_name = (snap.to_dict() or {}).get("name", "Unknown")
            
        # Standardize old vectors
        if isinstance(old_vectors, list) and len(old_vectors) > 0 and isinstance(old_vectors[0], (float, int)):
             old_vectors = [{"embedding": old_vectors, "dim": len(old_vectors)}]
        elif not isinstance(old_vectors, list):
             old_vectors = []
             
        # Create new vector entry with metadata indicating correction
        new_entry = {
            "embedding": new_vec,
            "dim": len(new_vec),
            "normalized": NORMALIZE_EMBEDDINGS,
            "metric": FAISS_METRIC,
            "created_at": datetime.now(timezone.utc),
            "source": "correction_feedback",
            "model": MODEL_NAME
        }
        
        old_vectors.append(new_entry)
        
        # Limit size (Keep last 10 for corrections to allow more variety)
        if len(old_vectors) > 10:
            old_vectors = old_vectors[-10:]
            
        doc_ref.set({
            "name": student_name,
            "mssv": correct_mssv,
            "vectors": old_vectors,
            "updated_at": firestore.SERVER_TIMESTAMP
        }, merge=True)
        
        # 3. Reload Manager (Hot Reload) to apply change immediately
        manager = TEACHER_MANAGERS.get(ctx_key)
        if manager and manager["index"]:
            idx = manager["index"]
            v_np = np.array([new_vec], dtype="float32")
            if NORMALIZE_EMBEDDINGS:
                v_np = _l2_normalize_np(v_np)
            idx.add(v_np)
            
            # Map new index ID to user
            new_id = idx.ntotal - 1
            manager["user_map"][new_id] = {
                "id": correct_mssv,
                "name": student_name,
                "mssv": correct_mssv
            }
            print(f"✅ Hot-update FAISS index. Total: {idx.ntotal}")

        return jsonify({
            "status": "success", 
            "message": f"Corrected {correct_mssv}. AI updated.",
            "new_total_vectors": len(old_vectors)
        })

    except Exception as e:
        print(f"Correction error: {e}")
        return jsonify({"error": str(e)}), 500

@app.route('/delete_profile', methods=['POST'])
@_require_api_key
def delete_profile():
    data = request.json or {}
    profile_key = (data.get("profile_key") or "").strip()
    if not profile_key:
        return jsonify({"status": "error", "message": "Missing profile_key"}), 400

    if not db:
        return jsonify({"error": "No DB"}), 500

    # Context
    teacher_id = get_teacher_id_from_req()
    term_id = get_term_id_from_req()
    class_id = get_class_id_from_req()

    face_col = get_face_collection(teacher_id, term_id, class_id)
    log_col = get_log_collection(teacher_id, term_id, class_id)
    roster_col = get_roster_collection(teacher_id, term_id, class_id)

    # Accept both formats: "name|mssv" OR "mssv"
    name = ""
    mssv = ""
    if "|" in profile_key:
        name, mssv = profile_key.split("|", 1)
        name = (name or "").strip()
        mssv = (mssv or "").strip()
    else:
        mssv = profile_key.strip()

    if not mssv:
        return jsonify({"status": "error", "message": "Missing mssv"}), 400

    # 1) Delete FaceVectors doc by id (new format)
    deleted_docs = 0
    prefix = ""
    try:
        doc_ref = db.collection(face_col).document(mssv)
        snap = doc_ref.get()
        if snap.exists:
            d = snap.to_dict() or {}
            # Try to resolve name/prefix from stored data
            if not name:
                name = str(d.get("name", "") or "").strip()
            prefix = str(d.get("prefix", "") or "").strip()
            if (not prefix) and d.get("storage_path"):
                import os
                prefix = os.path.dirname(str(d.get("storage_path"))) + "/"
            doc_ref.delete()
            deleted_docs += 1
    except Exception as e:
        print(f"⚠️ Delete Face doc by id error: {e}")

    # 1b) Legacy cleanup: delete any docs that still match MSSV (older format)
    try:
        q = db.collection(face_col).where("mssv", "==", mssv)
        if name:
            q = q.where("name", "==", name)
        for doc in q.stream():
            # avoid double delete if it's the same id
            if doc.id != mssv:
                doc.reference.delete()
                deleted_docs += 1
    except Exception as e:
        print(f"⚠️ Legacy Face doc cleanup error: {e}")

    # 1.5) Update roster -> registered=false OR delete if requested
    delete_roster = bool(data.get("delete_roster") or data.get("hard_delete") or data.get("delete_student"))
    try:
        if delete_roster:
             db.collection(roster_col).document(mssv).delete()
        else:
             db.collection(roster_col).document(mssv).set({
                "registered": False,
                "updated_at": firestore.SERVER_TIMESTAMP
             }, merge=True)
    except:
        pass

    # 2) Delete Storage photos
    deleted_files = 0
    try:
        if bucket:
            # If we couldn't get prefix from vector doc, try build from name+mssv as fallback
            if not prefix:
                if not name:
                    # try roster name
                    snap = db.collection(roster_col).document(mssv).get()
                    if snap.exists:
                        rd = snap.to_dict() or {}
                        name = (rd.get("name") or "").strip()
                if name:
                    prefix = get_photo_prefix(teacher_id, term_id, class_id, name, mssv)

            if prefix:
                for b in bucket.list_blobs(prefix=prefix):
                    b.delete()
                    deleted_files += 1
    except Exception as e:
        print(f"⚠️ Delete storage error: {e}")

    # 2.5) Delete attendance logs by MSSV
    deleted_logs = 0
    try:
        logs_query = db.collection(log_col).where("mssv", "==", mssv).stream()
        for log_doc in logs_query:
            log_doc.reference.delete()
            deleted_logs += 1
    except Exception as e:
        print(f"⚠️ Xóa logs lỗi: {e}")

    # 3) Rebuild RAM
    load_teacher_data(teacher_id, term_id, class_id)

    return jsonify({
        "status": "success",
        "deleted_docs": deleted_docs,
        "deleted_files": deleted_files,
        "deleted_logs": deleted_logs,
    }), 200

@app.route('/logs', methods=['GET'])
def get_logs():
    try:
        limit = int(request.args.get("limit", "200"))
        limit = max(1, min(limit, 500))
        
        # [NEW] Get context (teacher/term/class)
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_log_collection(teacher_id, term_id, class_id)

        if not db:
            return jsonify({"logs": []}), 200

        q = db.collection(col_path)\
              .order_by("vn_epoch_ms", direction=firestore.Query.DESCENDING)\
              .limit(limit)

        logs = []
        for d in q.stream():
            x = d.to_dict() or {}
            logs.append({
                "id": d.id,
                "name": x.get("name", ""),
                "mssv": x.get("mssv", ""),
                "date": datetime.strptime(x.get("date", ""), "%Y-%m-%d").strftime("%d/%m/%Y") if "-" in x.get("date", "") else x.get("date", ""),
                "time": x.get("time", ""),
                "status": x.get("status", "Present"),
                "distance": float(x.get("distance", 0.0) or 0.0),
                "match_percent": float(x.get("match_percent", 0.0) or 0.0),
                "vn_epoch_ms": int(x.get("vn_epoch_ms", 0) or 0),
            })

        return jsonify({"count": len(logs), "logs": logs}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/delete_log', methods=['POST'])
@_require_api_key
def delete_log():
    try:
        data = request.json or {}
        log_id = data.get("log_id", "")
        if not log_id:
            return jsonify({"error": "Missing log_id"}), 400
        
        # [NEW] Get context
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_log_collection(teacher_id, term_id, class_id)

        if db:
            db.collection(col_path).document(log_id).delete()
        return jsonify({"status": "success", "message": f"Deleted {log_id}"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/log_attendance', methods=['POST'])
@_require_api_key
def log_attendance():
    try:
        data = request.json
        if not data: return jsonify({"error": "No JSON data"}), 400

        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_log_collection(teacher_id, term_id, class_id)

        mssv = data.get("mssv")
        name = data.get("name")
        time_str = data.get("time")
        date_str = data.get("date")
        status = data.get("status", "Present")
        manual = data.get("manual", False)
        
        if not mssv or not date_str:
             return jsonify({"error": "Missing mssv or date"}), 400
        
        try:
             # Unity sends dd/MM/yyyy. Convert to YYYY-MM-DD for ID
             dt_obj = datetime.strptime(date_str, "%d/%m/%Y")
             date_iso = dt_obj.strftime("%Y-%m-%d")
        except:
             date_iso = datetime.now(VN_TZ).strftime("%Y-%m-%d")

        doc_id = f"{date_iso}_{mssv}"
        doc_ref = db.collection(col_path).document(doc_id)
        
        payload = {
            "name": name,
            "mssv": mssv,
            "date": date_iso, 
            "time": time_str,
            "status": status,
            "manual": manual,
            "confidence": data.get("confidence", 100.0),
            "vn_epoch_ms": int(time.time() * 1000), 
            "created_at": firestore.SERVER_TIMESTAMP,
            "updated_at": firestore.SERVER_TIMESTAMP,
            "source": "manual_app" if manual else "client_verify"
        }
        
        tx = db.transaction()
        _upsert_attendance(tx, doc_ref, payload)
        
        return jsonify({"status": "success", "message": "Log saved", "log_id": doc_id}), 200

    except Exception as e:
        print(f"Log error: {e}")
        return jsonify({"error": str(e)}), 500

# --- ROSTER ROUTES ---
@app.route("/roster", methods=["GET"])
def roster_get():
    try:
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_roster_collection(teacher_id, term_id, class_id)

        if db is None:
            return jsonify({"status": "success", "students": []})

        docs = db.collection(col_path).stream()
        students = []
        for d in docs:
            x = d.to_dict() or {}
            students.append({
                "mssv": x.get("mssv", d.id),
                "name": x.get("name", ""),
                "registered": bool(x.get("registered", False))
            })

        students.sort(key=lambda s: s.get("mssv", ""))
        return jsonify({"status": "success", "students": students})
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@app.route("/roster/import", methods=["POST"])
@_require_api_key
def roster_import():
    try:
        payload = request.json or {}
        arr = payload.get("students") or []
        if not isinstance(arr, list) or len(arr) == 0:
            return jsonify({"error": "Missing students[]"}), 400

        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_roster_collection(teacher_id, term_id, class_id)

        if db is None:
            return jsonify({"status": "success", "count": len(arr)})

        batch = db.batch()
        ops = 0
        count = 0

        for s in arr:
            name = _norm(s.get("name"))
            mssv = _norm(s.get("mssv"))
            if not name or not mssv:
                continue

            doc_ref = db.collection(col_path).document(mssv)
            batch.set(doc_ref, {
                "name": name,
                "mssv": mssv,
                "registered": bool(s.get("registered", False)),
                "updated_at": firestore.SERVER_TIMESTAMP,
                "created_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)

            ops += 1
            count += 1
            if ops >= 450:
                batch.commit()
                batch = db.batch()
                ops = 0

        if ops > 0:
            batch.commit()

        return jsonify({"status": "success", "count": count})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

# =========================
# SCHEDULE BUNDLE (SYNC)
# =========================
def get_schedule_bundle_doc(teacher_id: str):
    if not teacher_id:
        return None
    # store per teacher (bundle includes schoolYear/semester/phase + sessions + selectedClass/Term)
    return f"{TEACHERS_COLLECTION}/{teacher_id}/ScheduleBundle/main"

@app.route("/schedule_bundle", methods=["GET"])
def schedule_bundle_get():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"status": "error", "message": "Missing X-Teacher-ID"}), 400
        if not db:
            return jsonify({"status": "success", "sessions": []}), 200

        doc_path = get_schedule_bundle_doc(teacher_id)
        snap = db.document(doc_path).get()
        if not snap.exists:
            return jsonify({"status": "success", "sessions": []}), 200

        data = snap.to_dict() or {}
        data["status"] = "success"
        return jsonify(data), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500

@app.route("/schedule_bundle", methods=["POST"])
@_require_api_key
def schedule_bundle_set():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"status": "error", "message": "Missing X-Teacher-ID"}), 400
        if not db:
            return jsonify({"status": "error", "message": "Firestore not initialized"}), 500

        body = request.json or {}

        allow = {
            "sessions",
            "schoolYear", "semester",
            "phase1Start", "phase1End", "phase2Start", "phase2End",
            "selectedClassCode", "selectedTermId"
        }
        payload = {k: body.get(k) for k in allow if k in body}
        payload["updated_at"] = firestore.SERVER_TIMESTAMP

        doc_path = get_schedule_bundle_doc(teacher_id)
        db.document(doc_path).set(payload, merge=True)

        return jsonify({"status": "success"}), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500

import re



# ============================================================
# GOOGLE OAUTH + GOOGLE SHEETS (teacher + term + class)
# 1 file spreadsheet / (teacher, term, class)
# 1 tab / subjectCode
# Header: MSSV, HoTen, D1-T1..D1-T9, D2-T1..D2-T9
# ============================================================

from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import Flow
from google.auth.transport.requests import Request as GoogleRequest
from googleapiclient.discovery import build
from googleapiclient.errors import HttpError

GOOGLE_CLIENT_ID = os.getenv("GOOGLE_CLIENT_ID", "").strip()
GOOGLE_CLIENT_SECRET = os.getenv("GOOGLE_CLIENT_SECRET", "").strip()
GOOGLE_REDIRECT_URI = os.getenv("GOOGLE_REDIRECT_URI", "").strip()

GOOGLE_SCOPES = [
    "https://www.googleapis.com/auth/spreadsheets",
    "https://www.googleapis.com/auth/drive.file",
    "openid",
    "https://www.googleapis.com/auth/userinfo.email",
]

def _google_client_config():
    if not GOOGLE_CLIENT_ID or not GOOGLE_CLIENT_SECRET or not GOOGLE_REDIRECT_URI:
        return None
    return {
        "web": {
            "client_id": GOOGLE_CLIENT_ID,
            "client_secret": GOOGLE_CLIENT_SECRET,
            "auth_uri": "https://accounts.google.com/o/oauth2/auth",
            "token_uri": "https://oauth2.googleapis.com/token",
            "redirect_uris": [GOOGLE_REDIRECT_URI],
        }
    }

def _now_epoch():
    return int(time.time())

def _safe(s, default=""):
    s = (s or "").strip()
    return s if s else default

def _parse_vn_date(s):
    if not s: return None
    try:
        return datetime.strptime(s.strip(), "%d/%m/%Y").date()
    except:
        return None

def _parse_iso_date(s):
    if not s: return None
    try:
        return datetime.strptime(s.strip(), "%Y-%m-%d").date()
    except:
        return None

def _monday_of_week(d):
    return d - timedelta(days=d.weekday())

def _phase_week_for_date(d, cfg: dict):
    p1s = _parse_vn_date(cfg.get("phase1Start"))
    p2s = _parse_vn_date(cfg.get("phase2Start"))
    p1e = _parse_vn_date(cfg.get("phase1End"))
    p2e = _parse_vn_date(cfg.get("phase2End"))
    if p1s:
        m1 = _monday_of_week(p1s)
        w = ((d - m1).days // 7) + 1
        if (p1e and d <= p1e) or (not p1e and w >= 1):
            w = max(1, min(9, w))
            return 1, w
    if p2s:
        m2 = _monday_of_week(p2s)
        w = ((d - m2).days // 7) + 1
        if (p2e and d <= p2e) or (not p2e and w >= 1):
            w = max(1, min(9, w))
            return 2, w
    return 1, 1

def _a1_col(n1):
    s = ""
    n = int(n1)
    while n > 0:
        n, r = divmod(n - 1, 26)
        s = chr(65 + r) + s
    return s

def _attendance_col(phase: int, week_no: int):
    phase = 1 if phase != 2 else 2
    week_no = max(1, min(9, int(week_no)))
    if phase == 1:
        col_idx = 3 + (week_no - 1)
    else:
        col_idx = 3 + 9 + (week_no - 1)
    return _a1_col(col_idx)

def _sanitize_sheet_title(title: str):
    t = (title or "").strip()
    t = re.sub(r"[:\\/?*\[\]]", "-", t)
    t = re.sub(r"\s+", " ", t).strip()
    if not t:
        t = "Mon"
    if len(t) > 95:
        t = t[:95].rstrip()
    return t

def _sheet_title_for_subject(subject_code: str, subject_name: str):
    code = _safe(subject_code)
    name = _safe(subject_name)
    if name and code:
        return _sanitize_sheet_title(f"{name} ({code})")
    if code:
        return _sanitize_sheet_title(code)
    return _sanitize_sheet_title(name or "Mon")

def _pending_doc(poll_token: str):
    # Top-level collection để path luôn hợp lệ: Collection/Doc
    return f"GoogleOAuthPending/{poll_token}"

def _teacher_oauth_doc(teacher_id: str):
    return f"{TEACHERS_COLLECTION}/{teacher_id}/GoogleOAuth/main"

def _tenant_gsheet_doc(teacher_id, term_id, class_id):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/gsheets/main"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/main"

def _tenant_gsheet_cfg_doc(teacher_id, term_id, class_id):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/gsheets/config"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/config"

def _tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key: str):
    root = get_class_root(teacher_id, term_id, class_id)
    sid = re.sub(r"[^a-zA-Z0-9_\-]", "_", session_key or "session")
    if root:
        return f"{root}/gsheets/main/synced_sessions/{sid}"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/main/synced_sessions/{sid}"

def _get_google_creds(teacher_id: str):
    if not db:
        return None, "Firestore not initialized"
    doc = db.document(_teacher_oauth_doc(teacher_id)).get()
    if not doc.exists:
        return None, "No Google OAuth token"
    data = doc.to_dict() or {}
    refresh_token = data.get("refresh_token")
    if not refresh_token:
        return None, "No refresh_token"
    creds = Credentials(
        token=data.get("token"),
        refresh_token=refresh_token,
        token_uri=data.get("token_uri") or "https://oauth2.googleapis.com/token",
        client_id=data.get("client_id") or GOOGLE_CLIENT_ID,
        client_secret=data.get("client_secret") or GOOGLE_CLIENT_SECRET,
        scopes=data.get("scopes") or GOOGLE_SCOPES,
    )
    try:
        if creds.expired and creds.refresh_token:
            creds.refresh(GoogleRequest())
            db.document(_teacher_oauth_doc(teacher_id)).set({
                "token": creds.token,
                "updated_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)
    except Exception as e:
        return None, f"Refresh token failed: {e}"
    return creds, None

def _sheets_service(creds: Credentials):
    return build("sheets", "v4", credentials=creds, cache_discovery=False)

def _oauth2_service(creds: Credentials):
    return build("oauth2", "v2", credentials=creds, cache_discovery=False)

def _ensure_spreadsheet_v2(teacher_id, term_id, class_id):
    if not db:
        raise RuntimeError("Firestore not initialized")
    term_id = _safe(term_id, "default_term")
    class_id = _safe(class_id, "default_class")
    creds, err = _get_google_creds(teacher_id)
    if err:
        return None, None, False, err
    doc_path = _tenant_gsheet_doc(teacher_id, term_id, class_id)
    snap = db.document(doc_path).get()
    if snap.exists:
        meta = snap.to_dict() or {}
        sid = meta.get("spreadsheet_id")
        surl = meta.get("spreadsheet_url")
        if sid and surl:
            return sid, surl, False, None
    svc = _sheets_service(creds)
    title = f"FaceCheckin - {teacher_id} - {term_id} - {class_id}"
    body = {"properties": {"title": title}}
    resp = svc.spreadsheets().create(body=body, fields="spreadsheetId,spreadsheetUrl").execute()
    sid = resp.get("spreadsheetId")
    surl = resp.get("spreadsheetUrl")
    db.document(doc_path).set({
        "spreadsheet_id": sid,
        "spreadsheet_url": surl,
        "teacher_id": teacher_id,
        "term_id": term_id,
        "class_id": class_id,
        "created_at": firestore.SERVER_TIMESTAMP,
        "updated_at": firestore.SERVER_TIMESTAMP,
    }, merge=True)
    return sid, surl, True, None

def _get_spreadsheet_sheets_v2(svc, spreadsheet_id):
    meta = svc.spreadsheets().get(spreadsheetId=spreadsheet_id).execute()
    sheets = meta.get("sheets", []) or []
    title_to_id = {}
    for sh in sheets:
        props = (sh.get("properties") or {})
        title_to_id[props.get("title")] = props.get("sheetId")
    return title_to_id

def _ensure_subject_tab_header_v2(svc, spreadsheet_id, tab_title: str):
    tab_title = _sanitize_sheet_title(tab_title)
    title_to_id = _get_spreadsheet_sheets_v2(svc, spreadsheet_id)
    requests = []
    if tab_title not in title_to_id:
        requests.append({
            "addSheet": {"properties": {"title": tab_title, "gridProperties": {"rowCount": 1000, "columnCount": 30}}}
        })
    if requests:
        svc.spreadsheets().batchUpdate(spreadsheetId=spreadsheet_id, body={"requests": requests}).execute()
    headers = ["MSSV", "Họ và tên"] + [f"Đ1-T{i}" for i in range(1, 10)] + [f"Đ2-T{i}" for i in range(1, 10)]
    last_col = _a1_col(len(headers))
    rng = f"'{tab_title}'!A1:{last_col}1"
    svc.spreadsheets().values().update(spreadsheetId=spreadsheet_id, range=rng, valueInputOption="RAW", body={"values": [headers]}).execute()
    try:
        title_to_id = _get_spreadsheet_sheets_v2(svc, spreadsheet_id)
        sheet_id = title_to_id.get(tab_title)
        if sheet_id is not None:
            svc.spreadsheets().batchUpdate(spreadsheetId=spreadsheet_id, body={"requests": [{"updateSheetProperties": {"properties": {"sheetId": sheet_id, "gridProperties": {"frozenRowCount": 1}}, "fields": "gridProperties.frozenRowCount"}}]}).execute()
    except Exception as e:
        print(f"[GSheets v2] Error freezing header row: {e}")
    return tab_title

def _sheet_row_map_v2(svc, spreadsheet_id, tab_title, max_rows=2000):
    rng = f"'{tab_title}'!A2:B{max_rows}"
    resp = svc.spreadsheets().values().get(spreadsheetId=spreadsheet_id, range=rng).execute()
    values = resp.get("values") or []
    row_map = {}
    last_row = 1
    for i, row in enumerate(values):
        mssv = _safe(row[0] if len(row) > 0 else "")
        if mssv:
            row_idx = 2 + i
            row_map[mssv] = row_idx
            last_row = row_idx
    return row_map, last_row

def _apply_attendance_updates_v2(svc, spreadsheet_id, tab_title, col_letter, rows):
    roster_updates = 0
    write_updates = []
    row_map, last_row = _sheet_row_map_v2(svc, spreadsheet_id, tab_title)
    next_row = last_row + 1
    for it in (rows or []):
        mssv = _safe(it.get("mssv"))
        name = _safe(it.get("name"))
        status = _safe(it.get("status"), "A").upper()
        if status not in ("P", "L", "A"):
            status = "A"
        if not mssv:
            continue
        if mssv in row_map:
            r = row_map[mssv]
            write_updates.append({"range": f"'{tab_title}'!B{r}", "values": [[name]]})
        else:
            r = next_row
            next_row += 1
            row_map[mssv] = r
            write_updates.append({"range": f"'{tab_title}'!A{r}:B{r}", "values": [[mssv, name]]})
            roster_updates += 1
        write_updates.append({"range": f"'{tab_title}'!{col_letter}{r}", "values": [[status]]})
    if write_updates:
        svc.spreadsheets().values().batchUpdate(spreadsheetId=spreadsheet_id, body={"valueInputOption": "RAW", "data": write_updates}).execute()
    return {"rows_written": len(rows or []), "cells_written": len(write_updates), "new_roster_rows": roster_updates}

# OAuth CONNECT (after teacher login)
@app.route("/google/oauth/start", methods=["GET"])
def google_oauth_start():
    try:
        cfg = _google_client_config()
        if not cfg:
            return jsonify({"error": "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI"}), 500
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"error": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        poll_token = uuid.uuid4().hex
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        auth_url, _ = flow.authorization_url(access_type="offline", include_granted_scopes="true", prompt="consent", state=poll_token)
        if not db:
            return jsonify({"error": "Firestore not initialized"}), 500
        db.document(_pending_doc(poll_token)).set({"flow": "connect", "teacher_id": teacher_id, "term_id": term_id, "class_id": class_id, "status": "pending", "created_at": firestore.SERVER_TIMESTAMP, "expires_at": _now_epoch() + 600}, merge=True)
        return jsonify({"authorization_url": auth_url, "poll_token": poll_token}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/google/oauth/status", methods=["GET"])
def google_oauth_status():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        if not db:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        snap = db.document(_teacher_oauth_doc(teacher_id)).get()
        if not snap.exists:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        data = snap.to_dict() or {}
        has_rt = bool(data.get("refresh_token"))
        return jsonify({"connected": has_rt, "has_refresh_token": has_rt, "email": data.get("email")}), 200
    except Exception as e:
        return jsonify({"connected": False, "has_refresh_token": False, "error": str(e)}), 200

@app.route("/google/oauth/callback", methods=["GET"])
def google_oauth_callback():
    try:
        cfg = _google_client_config()
        if not cfg:
            return "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI", 500
        code = request.args.get("code")
        state = request.args.get("state")
        err = request.args.get("error")
        if err:
            return f"OAuth error: {err}", 400
        if not code or not state:
            return "Missing code/state", 400
        if not db:
            return "Firestore not initialized", 500
        pend_ref = db.document(_pending_doc(state))
        pend = pend_ref.get()
        if not pend.exists:
            return "Invalid/expired state", 400
        pend_data = pend.to_dict() or {}
        if pend_data.get("expires_at", 0) < _now_epoch():
            return "State expired", 400
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES, state=state)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        flow.fetch_token(code=code)
        creds = flow.credentials
        oauth = _oauth2_service(creds)
        me = oauth.userinfo().get().execute()
        email = me.get("email")
        flow_type = pend_data.get("flow") or "connect"
        if flow_type == "connect":
            teacher_id = pend_data.get("teacher_id")
            if not teacher_id:
                return "Missing teacher_id in pending", 400
            db.document(_teacher_oauth_doc(teacher_id)).set({"email": email, "token": creds.token, "refresh_token": creds.refresh_token or pend_data.get("refresh_token"), "token_uri": creds.token_uri, "client_id": GOOGLE_CLIENT_ID, "client_secret": GOOGLE_CLIENT_SECRET, "scopes": list(creds.scopes) if creds.scopes else GOOGLE_SCOPES, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
            pend_ref.set({"status": "done", "email": email, "has_refresh_token": bool(creds.refresh_token), "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
            return "✅ FaceCheckin: Kết nối Google thành công. Bạn có thể đóng tab này.", 200
        # login flow:
        _complete_login_flow_if_needed(state, creds, email)
        return "✅ FaceCheckin: Đăng nhập Google thành công. Bạn có thể đóng tab này.", 200
    except Exception as e:
        return f"Callback error: {e}", 500

# OAuth LOGIN (login screen)
@app.route("/auth/google/start", methods=["GET"])
def auth_google_start():
    try:
        cfg = _google_client_config()
        if not cfg:
            return jsonify({"error": "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI"}), 500
        poll_token = uuid.uuid4().hex
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        auth_url, _ = flow.authorization_url(access_type="offline", include_granted_scopes="true", prompt="consent", state=poll_token)
        if not db:
            return jsonify({"error": "Firestore not initialized"}), 500
        db.document(_pending_doc(poll_token)).set({"flow": "login", "status": "pending", "created_at": firestore.SERVER_TIMESTAMP, "expires_at": _now_epoch() + 600}, merge=True)
        return jsonify({"authorization_url": auth_url, "poll_token": poll_token}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/auth/google/poll", methods=["GET"])
def auth_google_poll():
    try:
        poll_token = request.args.get("p") or request.args.get("poll_token") or ""
        poll_token = poll_token.strip()
        if not poll_token:
            return jsonify({"status": "error", "error": "Missing poll_token"}), 400
        if not db:
            return jsonify({"status": "error", "error": "Firestore not initialized"}), 500
        snap = db.document(_pending_doc(poll_token)).get()
        if not snap.exists:
            return jsonify({"status": "error", "error": "Invalid poll_token"}), 404
        data = snap.to_dict() or {}
        st = data.get("status", "pending")
        return jsonify({"status": st, "teacher_id": data.get("teacher_id"), "email": data.get("email"), "has_refresh_token": bool(data.get("has_refresh_token")), "message": data.get("message"), "error": data.get("error")}), 200
    except Exception as e:
        return jsonify({"status": "error", "error": str(e)}), 500

@app.route("/auth/google/callback", methods=["GET"])
def auth_google_callback():
    return google_oauth_callback()

def _complete_login_flow_if_needed(state: str, creds, email: str):
    if not db:
        return
    pend_ref = db.document(_pending_doc(state))
    pend = pend_ref.get()
    if not pend.exists:
        return
    pend_data = pend.to_dict() or {}
    if (pend_data.get("flow") or "") != "login":
        return
    teacher_id = (email or "").strip().lower()
    if not teacher_id:
        pend_ref.set({"status": "error", "error": "No email"}, merge=True)
        return
    t_ref = db.collection(TEACHERS_COLLECTION).document(teacher_id)
    if not t_ref.get().exists:
        t_ref.set({"username": teacher_id, "password": "", "email": email, "created_at": firestore.SERVER_TIMESTAMP}, merge=True)
    db.document(_teacher_oauth_doc(teacher_id)).set({"email": email, "token": creds.token, "refresh_token": creds.refresh_token, "token_uri": creds.token_uri, "client_id": GOOGLE_CLIENT_ID, "client_secret": GOOGLE_CLIENT_SECRET, "scopes": list(creds.scopes) if creds.scopes else GOOGLE_SCOPES, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
    pend_ref.set({"status": "done", "teacher_id": teacher_id, "email": email, "has_refresh_token": bool(creds.refresh_token), "message": "Login OK", "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)

# Google Sheets v2 endpoints
@app.route("/gsheets/ensure_v2", methods=["GET"])
@_require_api_key
def gsheets_ensure_v2():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "need_google_auth": True, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "need_google_auth": True, "message": err, "spreadsheet_url": ""}), 200
        return jsonify({"ok": True, "status": "success", "url": surl, "spreadsheet_url": surl, "spreadsheet_id": sid, "created": created}), 200
    except Exception as e:
        return jsonify({"ok": False, "message": str(e)}), 500

@app.route("/gsheets/update_week", methods=["POST"])
@_require_api_key
def gsheets_update_week():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        body = request.json or {}
        session_key = _safe(body.get("sessionKey"))
        subject_code = _safe(body.get("subjectCode"))
        subject_name = _safe(body.get("subjectName"))
        session_date = _parse_iso_date(body.get("sessionDate"))

        if not session_key or not subject_code or not session_date:
            return jsonify({"ok": False, "message": "Missing sessionKey/subjectCode/sessionDate"}), 400

        # Idempotency
        if db:
             synced_ref = db.document(_tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key))
             if synced_ref.get().exists:
                 # Fetch existing spreadsheet_url
                 meta = db.document(_tenant_gsheet_doc(teacher_id, term_id, class_id)).get()
                 surl = (meta.to_dict() or {}).get("spreadsheet_url", "") if meta.exists else ""
                 return jsonify({"ok": True, "status": "success", "already_synced": True, "spreadsheet_url": surl}), 200

        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "message": err, "spreadsheet_url": ""}), 200

        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)

        # Config phase
        cfg = {}
        if db:
            cfg_snap = db.document(_tenant_gsheet_cfg_doc(teacher_id, term_id, class_id)).get()
            if cfg_snap.exists:
                cfg = cfg_snap.to_dict() or {}

        phase, week_no = _phase_week_for_date(session_date, cfg or {})
        col = _attendance_col(phase, week_no)

        tab = _sheet_title_for_subject(subject_code, subject_name)
        tab = _ensure_subject_tab_header_v2(svc, sid, tab)

        rows = body.get("rows") or []
        result = _apply_attendance_updates_v2(svc, sid, tab, col, rows)

        if db:
            db.document(_tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key)).set({
                "sessionKey": session_key,
                "subjectCode": subject_code,
                "subjectName": subject_name,
                "sessionDate": session_date.strftime("%Y-%m-%d"),
                "phase": phase,
                "week": week_no,
                "col": col,
                "updated_at": firestore.SERVER_TIMESTAMP,
                "created_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)

        return jsonify({
            "ok": True,
            "status": "success",
            "spreadsheet_url": surl,
            "phase": phase,
            "week": week_no,
            "col": col,
            "result": result
        }), 200

    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e), "error": str(e)}), 500

@app.route("/gsheets/finalize_v2", methods=["POST"])
@_require_api_key
def gsheets_finalize_v2():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        body = request.json or {}
        week = int(body.get("week") or 1)
        sheet_title = _sanitize_sheet_title(body.get("sheet_title") or "")
        updates = body.get("updates") or []
        if not sheet_title:
            return jsonify({"ok": False, "message": "Missing sheet_title"}), 400
        if week <= 9:
            phase, week_no = 1, week
        else:
            phase, week_no = 2, max(1, min(9, week - 9))
        col = _attendance_col(phase, week_no)
        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "message": err}), 200
        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)
        sheet_title = _ensure_subject_tab_header_v2(svc, sid, sheet_title)
        rows = []
        for u in updates:
            mssv = _safe(u.get("mssv"))
            name = _safe(u.get("name"))
            val = _safe(u.get("value"), "A").upper()
            if val not in ("P", "L", "A"):
                val = "A"
            if mssv:
                rows.append({"mssv": mssv, "name": name, "status": val})
        result = _apply_attendance_updates_v2(svc, sid, sheet_title, col, rows)
        updated = int((result or {}).get("rows_written", 0))
        return jsonify({"ok": True, "status": "success", "spreadsheet_url": surl, "phase": phase, "week": week_no, "col": col, "result": result, "updated": updated, "message": "Finalized"}), 200
    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e), "error": str(e)}), 500

# --- BACKFILL STORAGE_PATH ---
@app.route('/backfill_storage_path', methods=['POST'])
def backfill_storage_path():
    """
    Quét tất cả FaceVectors và cập nhật storage_path cho các document thiếu.
    Chỉ cần gọi 1 lần sau khi deploy.
    """
    try:
        if not db or not bucket:
            return jsonify({"status": "error", "message": "Firebase not initialized"}), 500
        
        def find_first_photo(prefix):
            for blob in bucket.list_blobs(prefix=prefix, max_results=1):
                return blob.name
            return None
        
        updated_count = 0
        skipped_count = 0
        not_found_count = 0
        errors = []
        
        # Lấy tất cả Teachers
        teachers = db.collection("Teachers").stream()
        
        for teacher_doc in teachers:
            teacher_id = teacher_doc.id
            
            # Lấy tất cả terms
            terms = db.collection("Teachers").document(teacher_id).collection("terms").stream()
            
            for term_doc in terms:
                term_id = term_doc.id
                
                # Lấy tất cả classes
                classes = db.collection("Teachers").document(teacher_id).collection("terms").document(term_id).collection("classes").stream()
                
                for class_doc in classes:
                    class_id = class_doc.id
                    
                    # Lấy tất cả FaceVectors
                    face_col = f"Teachers/{teacher_id}/terms/{term_id}/classes/{class_id}/FaceVectors"
                    face_docs = db.collection(face_col).stream()
                    
                    for face_doc in face_docs:
                        doc_id = face_doc.id
                        data = face_doc.to_dict() or {}
                        
                        # Bỏ qua nếu đã có storage_path
                        existing_path = (data.get("storage_path") or "").strip()
                        if existing_path:
                            skipped_count += 1
                            continue
                        
                        name = (data.get("name") or "").strip()
                        mssv = (data.get("mssv") or doc_id).strip()
                        
                        if not name or not mssv:
                            skipped_count += 1
                            continue
                        
                        # Tìm ảnh trong Storage
                        prefix = f"Teachers/{teacher_id}/terms/{term_id}/classes/{class_id}/photos/{name}_{mssv}/"
                        photo_path = find_first_photo(prefix)
                        
                        # Thử với underscore thay vì space
                        if not photo_path and " " in prefix:
                            prefix_alt = prefix.replace(" ", "_")
                            photo_path = find_first_photo(prefix_alt)
                        
                        if photo_path:
                            db.collection(face_col).document(doc_id).update({
                                "storage_path": photo_path
                            })
                            updated_count += 1
                        else:
                            not_found_count += 1
        
        return jsonify({
            "status": "success",
            "updated": updated_count,
            "skipped": skipped_count,
            "not_found": not_found_count,
            "message": f"Backfill completed: {updated_count} updated, {skipped_count} skipped, {not_found_count} not found"
        }), 200
        
    except Exception as e:
        traceback.print_exc()
        return jsonify({"status": "error", "message": str(e)}), 500

# --- WARMUP ---
def warmup():
    print("🔥 Warmup Thread Started...")
    # try:
    #     print(f"🔥 DeepFace: Loading {MODEL_NAME}...")
    #     DeepFace.build_model(MODEL_NAME)
    #     print(f"🔥 DeepFace: {MODEL_NAME} Loaded.")
    # except Exception as e:
    #     print(f"❌ Warmup Model Error: {e}")
    
    # Optional: Load some default data or keep empty
    # build_faiss_index() -> REMOVED/UPDATED
    # We can load 'global' context if needed
    # load_teacher_data(None)
    print("🔥 Warmup Finished.")

if __name__ == "__main__":
    if WARMUP_ON_START:
        threading.Thread(target=warmup, daemon=True).start()
    else:
        # Load legacy global data on start if any
        load_teacher_data(None)

    print(f"🚀 Server running on port 5050 | Metric: {FAISS_METRIC} | Norm: {NORMALIZE_EMBEDDINGS}")
    port = int(os.getenv("PORT", "5050"))
    print(f"🚀 Server running on port {port} | Metric: {FAISS_METRIC} | Norm: {NORMALIZE_EMBEDDINGS}")
    app.run(host="0.0.0.0", port=port, debug=False)