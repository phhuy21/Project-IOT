"""
core/faiss_engine.py — FAISS index management & auto-learning.
Được tách từ main.py dòng 477-640.
"""
import traceback
import threading
import numpy as np
import faiss
from datetime import datetime, timezone

from config.settings import (
    INDEX_LOCK, TEACHER_MANAGERS, FAISS_METRIC, NORMALIZE_EMBEDDINGS, MODEL_NAME, db
)
from core.multitenancy import make_context_key, get_face_collection
from core.helpers import _l2_normalize_np


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
        
        from firebase_admin import firestore
        doc_ref.set({"vectors": old_vectors, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
        print(f"🧠 Auto-learned for {mssv}. Total: {len(old_vectors)}")
        
    except Exception as e:
        print(f"❌ Auto-learn failed for {mssv}: {e}")
