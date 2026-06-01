"""
api/verify.py — Route /verify (pipeline nhận diện chính).
Được tách từ main.py dòng 642-1017.
"""
import os
import time
import base64
import threading
import traceback
import numpy as np
from flask import Blueprint, request, jsonify

from config.settings import (
    TEACHER_MANAGERS, FAISS_METRIC, NORMALIZE_EMBEDDINGS,
    COSINE_MIN, COSINE_GAP_MIN, COSINE_RATIO_MAX, MAX_VERIFY_FRAMES, db, bucket
)
from core.multitenancy import (
    get_teacher_id_from_req, get_term_id_from_req, get_class_id_from_req,
    make_context_key
)
from core.helpers import _ms, _l2_normalize_np, _clean_base64, write_attendance_once_per_day
from core.modal_client import call_modal_ai
from core.faiss_engine import load_teacher_data, auto_learn_face

verify_bp = Blueprint('verify', __name__)


@verify_bp.route('/verify', methods=['POST'])
def check_in():
    print("DEBUG: Request verify received!")
    verify_path = None
    verify_paths = []
    
    t_backend0 = _ms()
    t_embed = 0.0
    t_match = 0.0
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
            b64_list = b64_list[:MAX_VERIFY_FRAMES]
            if (not isinstance(b64_list, list)) or (len(b64_list) == 0):
                return jsonify({"error": "imgs_base64/images must be a non-empty list"}), 400

            dfs = []
            for i, b64_item in enumerate(b64_list):
                try:
                    b64_str = _clean_base64(b64_item)
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
        if manager and class_id and class_id != "default_class":
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
            conf = d.get("confidence", 1.0)
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
            
        dfs = high_quality_dfs

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
                if current_dim == 0:
                     load_teacher_data(teacher_id, term_id, class_id)
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

                # ===== ID-AWARE SEARCH: group by MSSV =====
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

                search_k = min(20, ntotal)
                
                t0_match = _ms()
                D, I = faiss_index.search(target_np, k=search_k)
                t_match += (_ms() - t0_match)

                best_by_mssv = {}
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
                        if (mssv_key not in best_by_mssv) or (val > best_by_mssv[mssv_key][0]):
                            best_by_mssv[mssv_key] = (val, idx)
                    else:
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
                    
                    if k == 1:
                        is_match = (best_sim >= COSINE_MIN)
                    else:
                        is_match = (best_sim >= COSINE_MIN) and (gap_sim >= COSINE_GAP_MIN) and (ratio_sim <= COSINE_RATIO_MAX)

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

                # Ghi Log Attendance
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
