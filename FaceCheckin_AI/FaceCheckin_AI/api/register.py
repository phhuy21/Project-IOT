"""
api/register.py — Routes /register, /reload, /version.
Được tách từ main.py dòng 1018-1237.
"""
import os
import time
import base64
import traceback
import numpy as np
from datetime import datetime, timezone
from concurrent.futures import ThreadPoolExecutor, as_completed

from flask import Blueprint, request, jsonify
from firebase_admin import firestore

from config.settings import (
    db, bucket, FAISS_METRIC, NORMALIZE_EMBEDDINGS,
    MODEL_NAME, DETECTOR_BACKEND, TEACHER_MANAGERS
)
from core.multitenancy import (
    get_teacher_id_from_req, get_term_id_from_req, get_class_id_from_req,
    make_context_key, get_face_collection, get_roster_collection, get_photo_prefix
)
from core.helpers import _require_api_key, _clean_base64, calculate_average_embedding
from core.modal_client import call_modal_ai
from core.faiss_engine import load_teacher_data

register_bp = Blueprint('register', __name__)


@register_bp.route('/reload', methods=['GET'])
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


@register_bp.route('/register', methods=['POST'])
@_require_api_key
def register_face():
    local_path = None
    try:
        data = request.get_json(silent=True)
        imgs = []

        # [NEW] Support Multipart/Form-Data (Unity WWWForm)
        if data is None:
            data = request.form.to_dict()
            
            if 'images' in request.files:
                file_list = request.files.getlist('images')
                for f in file_list:
                    try:
                        content = f.read()
                        b64 = base64.b64encode(content).decode('utf-8')
                        imgs.append(b64)
                    except Exception as e:
                        print(f"Error reading file upload: {e}")

            if 'append_mode' in data:
                 val = str(data['append_mode']).lower()
                 data['append_mode'] = (val == 'true')

        elif 'imgs_base64' in data and isinstance(data['imgs_base64'], list):
            imgs = data['imgs_base64']
        elif 'images' in data and isinstance(data['images'], list):
             imgs = data['images']
        elif 'img_base64' in data:
            imgs = [data['img_base64']]
            
        if not imgs or 'name' not in data:
            return jsonify({"error": "Missing info or images (name required)"}), 400

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
        
        doc_ref = db.collection(face_col).document(mssv)
        snap = doc_ref.get()
        old_vectors = []
        if snap.exists:
             old_vectors = (snap.to_dict() or {}).get("vectors", []) or []

        if isinstance(old_vectors, list) and len(old_vectors) > 0 and isinstance(old_vectors[0], (float, int)):
             old_vectors = [{
                 "embedding": old_vectors,
                 "dim": len(old_vectors),
                 "metric": FAISS_METRIC
             }]
        elif not isinstance(old_vectors, list):
             old_vectors = []

        collected_vectors = []
        success_count = 0
        
        def process_single_image(idx, img_b64):
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
                modal_result = call_modal_ai(img_str, enable_restore=True)

                print(f"📋 Modal result for img {idx}: {modal_result}")

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

        if collected_vectors:
            print(f"🔹 Averaging {len(collected_vectors)} vectors for {mssv}...")
            final_vector = calculate_average_embedding(collected_vectors)
            
            new_entry = {
                "embedding": final_vector,
                "dim": len(final_vector),
                "normalized": NORMALIZE_EMBEDDINGS,
                "model": MODEL_NAME,
                "detector": DETECTOR_BACKEND,
                "metric": FAISS_METRIC,
                "created_at": datetime.now(timezone.utc),
                "storage_path": ""
            }
            append_mode = data.get('append_mode', False)
            
            if append_mode:
                print(f"🔹 Append Mode: Adding new vector to existing {len(old_vectors)} vectors.")
                old_vectors.append(new_entry)
            else:
                old_vectors = [new_entry] 

        if success_count == 0:
             return jsonify({"error": "No valid faces found in any image"}), 400

        MAX_VECTORS = int(os.getenv("MAX_VECTORS_PER_PROFILE", "5"))
        if MAX_VECTORS > 0 and len(old_vectors) > MAX_VECTORS:
             old_vectors = old_vectors[-MAX_VECTORS:]

        doc_ref.set({
            "name": name,
            "mssv": mssv,
            "vectors": old_vectors,
            "prefix": get_photo_prefix(teacher_id, term_id, class_id, name, mssv),
            "updated_at": firestore.SERVER_TIMESTAMP,
        }, merge=True)
        
        db.collection(roster_col).document(mssv).set({
            "name": name,
            "mssv": mssv,
            "registered": True,
            "updated_at": firestore.SERVER_TIMESTAMP,
        }, merge=True)
        
        load_teacher_data(teacher_id, term_id, class_id)
        
        return jsonify({"status": "success", "message": f"Registered {success_count} vectors for {name}"}), 200

    except Exception as e:
        print("Lỗi register:", e)
        traceback.print_exc()
        return jsonify({"status": "error", "message": str(e)}), 500
    finally:
        pass


@register_bp.route("/version", methods=["GET"])
def version():
    return jsonify({"version": os.getenv("APP_VERSION", "dev")}), 200
