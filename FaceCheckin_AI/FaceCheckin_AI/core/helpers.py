"""
core/helpers.py — Utility functions dùng chung.
Được tách từ main.py dòng 361-475.
"""
import time
import numpy as np
from datetime import datetime
from functools import wraps
from flask import request, jsonify
from firebase_admin import firestore

from config.settings import API_KEY, VN_TZ
from core.multitenancy import get_log_collection


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
    from config.settings import db
    
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
