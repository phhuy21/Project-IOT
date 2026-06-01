"""
api/profiles.py — Routes /profiles, /avatar_url, /correct_identity,
                   /delete_profile, /logs, /delete_log, /log_attendance.
Được tách từ main.py dòng 1234-1746.
"""
import os
import time
import traceback
import numpy as np
from datetime import datetime, timedelta

from flask import Blueprint, request, jsonify
from firebase_admin import firestore
import google.auth
from google.auth import impersonated_credentials

from config.settings import (
    db, bucket, TEACHER_MANAGERS, FAISS_METRIC, NORMALIZE_EMBEDDINGS,
    MODEL_NAME, TEACHERS_COLLECTION, VN_TZ
)
from core.multitenancy import (
    get_teacher_id_from_req, get_term_id_from_req, get_class_id_from_req,
    make_context_key, get_face_collection, get_roster_collection,
    get_log_collection, get_photo_prefix
)
from core.helpers import (
    _require_api_key, _clean_base64, _l2_normalize_np,
    _upsert_attendance, vn_now
)
from core.modal_client import call_modal_ai
from core.faiss_engine import load_teacher_data

profiles_bp = Blueprint('profiles', __name__)


@profiles_bp.route('/profiles', methods=['GET'])
def list_profiles():
    groups = {} 
    try:
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        
        col_path = get_face_collection(teacher_id, term_id, class_id)

        if not db:
             return jsonify({"profiles": [], "count": 0}), 200

        docs = db.collection(col_path).stream()
        for doc in docs:
            d = doc.to_dict() or {}
            name = str(d.get("name", "Unknown") or "Unknown").strip()
            mssv = str(d.get("mssv", "NoID") or "NoID").strip()
            storage_path = d.get("storage_path", "")

            key = mssv
            if key not in groups:
                prefix = ""
                if storage_path:
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
            vecs = d.get("vectors")
            cnt = 0
            if isinstance(vecs, list):
                if len(vecs) > 0 and isinstance(vecs[0], (float, int)):
                    cnt = 1
                else:
                    cnt = len(vecs)
            elif vecs is not None:
                cnt = 1

            groups[key]["doc_count"] += cnt

        out = list(groups.values())
        out.sort(key=lambda x: (str(x.get("mssv", "")).strip(), str(x.get("name", "")).strip()))
        return jsonify({"profiles": out, "count": len(out)}), 200
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500


@profiles_bp.route('/avatar_url', methods=['GET'])
def avatar_url():
    try:
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        mssv = (request.args.get("mssv") or "").strip()
        name = (request.args.get("name") or "").strip()

        if not bucket or not mssv:
            return jsonify({"status": "error", "message": "Missing mssv or bucket not ready"}), 200

        if not name and db:
            roster_col = get_roster_collection(teacher_id, term_id, class_id)
            snap = db.collection(roster_col).document(mssv).get()
            if snap.exists:
                d = snap.to_dict() or {}
                name = (d.get("name") or "").strip()

        if db:
            face_col = get_face_collection(teacher_id, term_id, class_id)
            snap = db.collection(face_col).document(mssv).get()
            if snap.exists:
                d = snap.to_dict() or {}
                sp = (d.get("storage_path") or "").strip()
                if sp:
                    b = bucket.blob(sp)
                    
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


@profiles_bp.route('/correct_identity', methods=['POST'])
@_require_api_key
def correct_identity():
    """
    Feedback Loop Endpoint:
    Allows teacher to manually correct a face.
    """
    try:
        data = request.json
        if not data or 'img_base64' not in data or 'correct_mssv' not in data:
            return jsonify({"error": "Missing info"}), 400
            
        img_str = _clean_base64(data['img_base64'])
        correct_mssv = data['correct_mssv']
        student_name = data.get('student_name', 'Unknown')
        
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        ctx_key = make_context_key(teacher_id, term_id, class_id)
        
        print(f"🔧 Correcting identity for [{correct_mssv}] in ctx [{ctx_key}]...")

        modal_result = call_modal_ai(img_str, enable_restore=True)
        if not modal_result or "embedding" not in modal_result:
             return jsonify({"error": "Could not extract face for correction"}), 400
             
        new_vec = modal_result["embedding"]
        
        face_col = get_face_collection(teacher_id, term_id, class_id)
        doc_ref = db.collection(face_col).document(correct_mssv)
        
        snap = doc_ref.get()
        old_vectors = []
        if snap.exists:
            old_vectors = (snap.to_dict() or {}).get("vectors", []) or []
            if not student_name or student_name == 'Unknown':
                 student_name = (snap.to_dict() or {}).get("name", "Unknown")
            
        if isinstance(old_vectors, list) and len(old_vectors) > 0 and isinstance(old_vectors[0], (float, int)):
             old_vectors = [{"embedding": old_vectors, "dim": len(old_vectors)}]
        elif not isinstance(old_vectors, list):
             old_vectors = []
             
        new_entry = {
            "embedding": new_vec,
            "dim": len(new_vec),
            "normalized": NORMALIZE_EMBEDDINGS,
            "metric": FAISS_METRIC,
            "created_at": datetime.now(timezone.utc) if 'timezone' in dir() else datetime.utcnow(),
            "source": "correction_feedback",
            "model": MODEL_NAME
        }
        
        old_vectors.append(new_entry)
        
        if len(old_vectors) > 10:
            old_vectors = old_vectors[-10:]
            
        doc_ref.set({
            "name": student_name,
            "mssv": correct_mssv,
            "vectors": old_vectors,
            "updated_at": firestore.SERVER_TIMESTAMP
        }, merge=True)
        
        manager = TEACHER_MANAGERS.get(ctx_key)
        if manager and manager["index"]:
            idx = manager["index"]
            v_np = np.array([new_vec], dtype="float32")
            if NORMALIZE_EMBEDDINGS:
                v_np = _l2_normalize_np(v_np)
            idx.add(v_np)
            
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


@profiles_bp.route('/delete_profile', methods=['POST'])
@_require_api_key
def delete_profile():
    data = request.json or {}
    profile_key = (data.get("profile_key") or "").strip()
    if not profile_key:
        return jsonify({"status": "error", "message": "Missing profile_key"}), 400

    if not db:
        return jsonify({"error": "No DB"}), 500

    teacher_id = get_teacher_id_from_req()
    term_id = get_term_id_from_req()
    class_id = get_class_id_from_req()

    face_col = get_face_collection(teacher_id, term_id, class_id)
    log_col = get_log_collection(teacher_id, term_id, class_id)
    roster_col = get_roster_collection(teacher_id, term_id, class_id)

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

    deleted_docs = 0
    prefix = ""
    try:
        doc_ref = db.collection(face_col).document(mssv)
        snap = doc_ref.get()
        if snap.exists:
            d = snap.to_dict() or {}
            if not name:
                name = str(d.get("name", "") or "").strip()
            prefix = str(d.get("prefix", "") or "").strip()
            if (not prefix) and d.get("storage_path"):
                prefix = os.path.dirname(str(d.get("storage_path"))) + "/"
            doc_ref.delete()
            deleted_docs += 1
    except Exception as e:
        print(f"⚠️ Delete Face doc by id error: {e}")

    try:
        q = db.collection(face_col).where("mssv", "==", mssv)
        if name:
            q = q.where("name", "==", name)
        for doc in q.stream():
            if doc.id != mssv:
                doc.reference.delete()
                deleted_docs += 1
    except Exception as e:
        print(f"⚠️ Legacy Face doc cleanup error: {e}")

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

    deleted_files = 0
    try:
        if bucket:
            if not prefix:
                if not name:
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

    deleted_logs = 0
    try:
        logs_query = db.collection(log_col).where("mssv", "==", mssv).stream()
        for log_doc in logs_query:
            log_doc.reference.delete()
            deleted_logs += 1
    except Exception as e:
        print(f"⚠️ Xóa logs lỗi: {e}")

    load_teacher_data(teacher_id, term_id, class_id)

    return jsonify({
        "status": "success",
        "deleted_docs": deleted_docs,
        "deleted_files": deleted_files,
        "deleted_logs": deleted_logs,
    }), 200


@profiles_bp.route('/logs', methods=['GET'])
def get_logs():
    try:
        limit = int(request.args.get("limit", "200"))
        limit = max(1, min(limit, 500))
        
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


@profiles_bp.route('/delete_log', methods=['POST'])
@_require_api_key
def delete_log():
    try:
        data = request.json or {}
        log_id = data.get("log_id", "")
        if not log_id:
            return jsonify({"error": "Missing log_id"}), 400
        
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_log_collection(teacher_id, term_id, class_id)

        if db:
            db.collection(col_path).document(log_id).delete()
        return jsonify({"status": "success", "message": f"Deleted {log_id}"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@profiles_bp.route('/log_attendance', methods=['POST'])
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
