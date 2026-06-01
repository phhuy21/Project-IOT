import firebase_admin
from firebase_admin import credentials, firestore
import os

# 1. CẤU HÌNH
CRED_PATH = "serviceAccountKey.json" # Đảm bảo file này cùng thư mục
TARGET_TEACHER_ID = "admin"          # Tài khoản đích muốn nhận dữ liệu

# Khởi tạo Firebase nếu chưa có
if not firebase_admin._apps:
    if os.path.exists(CRED_PATH):
        cred = credentials.Certificate(CRED_PATH)
        firebase_admin.initialize_app(cred)
    else:
        print(f"❌ LỖI: Không tìm thấy file {CRED_PATH}!")
        print("Vui lòng đảm bảo file key nằm cùng thư mục với script này.")
        exit(1)

db = firestore.client()

def migrate_collection(source_col, target_col):
    print(f"🔄 Đang chuyển từ Collection global [{source_col}] sang [{TARGET_TEACHER_ID}/{target_col}]...")
    
    # Lấy reference tới collection nguồn
    source_ref = db.collection(source_col)
    docs = source_ref.stream()
    
    count = 0
    batch = db.batch()
    BATCH_SIZE = 400 # Firestore limit 500
    
    found_any = False
    
    for doc in docs:
        found_any = True
        data = doc.to_dict()
        
        # Tạo đường dẫn mới: Teachers/{admin}/{Collection}/{doc_id}
        target_ref = db.collection('Teachers').document(TARGET_TEACHER_ID).collection(target_col).document(doc.id)
        
        # Copy data
        batch.set(target_ref, data)
        count += 1
        
        if count % BATCH_SIZE == 0:
            batch.commit()
            batch = db.batch()
            print(f"   Saved {count} records...")

    # Commit số lẻ còn lại
    if count % BATCH_SIZE != 0:
        batch.commit()
        
    if not found_any:
        print(f"⚠️ Cảnh báo: Collection nguồn '{source_col}' trống hoặc không tồn tại.")
    else:
        print(f"✅ Xong! Đã chuyển {count} bản ghi từ {source_col}.")

# 2. CHẠY MIGRATE
if __name__ == "__main__":
    print("=====================================================")
    print(f"🚀 Bắt đầu Migrate dữ liệu cũ cho user: {TARGET_TEACHER_ID}")
    print("=====================================================")
    
    # 1. Chuyển dữ liệu khuôn mặt (Profiles)
    # Tên collection cũ trong code của bạn là 'FaceVectors'
    migrate_collection('FaceVectors', 'FaceVectors') 
    
    # 2. Chuyển dữ liệu điểm danh (Logs)
    # Tên collection cũ trong code của bạn là 'attendance_logs'
    migrate_collection('attendance_logs', 'attendance_logs')
    
    print("\n=====================================================")
    print("🎉 HOÀN TẤT! Dữ liệu đã được copy sang tài khoản admin.")
    print("Bây giờ hãy:")
    print("1. Update Server Cloud Run (Redeploy main.py)")
    print("2. Mở App -> Đăng ký tài khoản 'admin' (nếu chưa có)")
    print("3. Đăng nhập và kiểm tra dữ liệu.")
