"""
core/multitenancy.py — Multi-tenancy helpers (teacher/term/class).
Được tách từ main.py dòng 139-201.
"""
from flask import request
from config.settings import TEACHERS_COLLECTION, TERMS_COLLECTION, CLASSES_COLLECTION


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
