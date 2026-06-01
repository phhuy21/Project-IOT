import modal
import subprocess

# 1. Định nghĩa "cái hộp" chứa code (Image)
# Chúng ta dùng Python cơ bản, cài thêm thư viện để in màu cho đẹp
image = modal.Image.debian_slim()

app = modal.App("check-gpu-app")

# 2. Định nghĩa hàm chạy trên Cloud
# Quan trọng: gpu="T4" -> Yêu cầu Modal cấp 1 con GPU NVIDIA T4
@app.function(image=image, gpu="T4")
def check_nvidia():
    print("dang kiem tra GPU tren Cloud...")
    # Chạy lệnh 'nvidia-smi' của Linux để xem thông tin Card màn hình
    try:
        output = subprocess.check_output(["nvidia-smi"], text=True)
        return output
    except Exception as e:
        return f"Loi: {e}"

# 3. Phần này chạy trên máy tính của bạn để gọi lên Cloud
@app.local_entrypoint()
def main():
    print("🚀 Đang gửi lệnh lên Modal...")
    result = check_nvidia.remote() # Gọi hàm từ xa
    print("\n🎉 KẾT QUẢ TỪ CLOUD TRẢ VỀ:\n")
    print(result)
