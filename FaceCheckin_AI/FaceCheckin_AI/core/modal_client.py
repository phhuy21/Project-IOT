"""
core/modal_client.py — Giao tiếp với Modal GPU Worker.
Được tách từ main.py dòng 34-71.
"""
import requests

# ⚠️ SAU KHI CHẠY `modal deploy gpu_worker.py`, DÁN LINK MỚI VÀO DƯỚI ĐÂY.
# LƯU Ý: URL mới có tên class "faceinference" trong đường dẫn (do chuyển sang class-based).
# Nếu URL bị sai, hãy chạy lại `modal deploy gpu_worker.py` và copy URL từ output.
MODAL_API_URL = "https://phhuy21--face-recognition-backend-faceinference-process--b5c5a2.modal.run"

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
                print(f"⚠️ Modal AI trả về lỗi: status={result.get('status')}, message={result.get('message')}")
                return None
        else:
            print(f"❌ Lỗi kết nối Modal: {response.status_code} | body={response.text[:500]}")
            return None
            
    except Exception as e:
        print(f"❌ Lỗi gọi Modal: {str(e)}")
        return None
