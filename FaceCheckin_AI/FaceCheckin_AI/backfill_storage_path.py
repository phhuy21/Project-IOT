"""
Backfill storage_path for existing FaceVectors documents.

Chạy script này 1 lần để cập nhật storage_path cho các sinh viên đã đăng ký trước đó.
Sau khi chạy, avatar sẽ hiển thị đúng ở mọi lớp/kỳ.

Cách chạy:
    python backfill_storage_path.py
"""

import os
import firebase_admin
from firebase_admin import credentials, firestore, storage

# --- Config ---
BUCKET_NAME = "phamgiahuy-9ab1c.firebasestorage.app"
CRED_PATH = "serviceAccountKey.json"

# Init Firebase
if not firebase_admin._apps:
    if os.path.exists(CRED_PATH):
        cred = credentials.Certificate(CRED_PATH)
        firebase_admin.initialize_app(cred, {'storageBucket': BUCKET_NAME})
    else:
        firebase_admin.initialize_app(options={'storageBucket': BUCKET_NAME})

db = firestore.client()
bucket = storage.bucket()


def find_first_photo(prefix):
    """Tìm ảnh đầu tiên trong folder với prefix cho trước."""
    for blob in bucket.list_blobs(prefix=prefix, max_results=1):
        return blob.name
    return None


def backfill():
    """Quét tất cả FaceVectors và cập nhật storage_path nếu thiếu."""
    
    # Lấy tất cả Teachers
    teachers_ref = db.collection("Teachers")
    teachers = teachers_ref.stream()
    
    updated_count = 0
    skipped_count = 0
    not_found_count = 0
    
    for teacher_doc in teachers:
        teacher_id = teacher_doc.id
        print(f"\n📂 Teacher: {teacher_id}")
        
        # Lấy tất cả terms
        terms_ref = db.collection("Teachers").document(teacher_id).collection("terms")
        terms = terms_ref.stream()
        
        for term_doc in terms:
            term_id = term_doc.id
            print(f"  📅 Term: {term_id}")
            
            # Lấy tất cả classes
            classes_ref = terms_ref.document(term_id).collection("classes")
            classes = classes_ref.stream()
            
            for class_doc in classes:
                class_id = class_doc.id
                print(f"    📚 Class: {class_id}")
                
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
                        print(f"      ⚠️ Skip {doc_id}: Missing name/mssv")
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
                        # Cập nhật storage_path
                        db.collection(face_col).document(doc_id).update({
                            "storage_path": photo_path
                        })
                        print(f"      ✅ Updated {mssv}: {photo_path}")
                        updated_count += 1
                    else:
                        print(f"      ❌ Not found: {prefix}")
                        not_found_count += 1
    
    print(f"\n{'='*50}")
    print(f"📊 Kết quả:")
    print(f"   ✅ Đã cập nhật: {updated_count}")
    print(f"   ⏭️ Đã có sẵn (bỏ qua): {skipped_count}")
    print(f"   ❌ Không tìm thấy ảnh: {not_found_count}")
    print(f"{'='*50}")


if __name__ == "__main__":
    print("🚀 Bắt đầu backfill storage_path...")
    backfill()
    print("\n✅ Hoàn tất!")
