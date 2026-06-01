# ADR-003: Ngưỡng Cosine Similarity (0.55 / 0.03 / 0.97)

## Ngày: 2025-12

## Trạng thái: Accepted

## Bối cảnh
Cần xác định ngưỡng matching phù hợp cho lớp 50-100 sinh viên. Quá thấp → false positive (nhận nhầm). Quá cao → false negative (không nhận ra).

## Quyết định
Sử dụng **3 điều kiện** đồng thời:
- `COSINE_MIN = 0.55`: Similarity tối thiểu
- `COSINE_GAP_MIN = 0.03`: Khoảng cách giữa top-1 và top-2
- `COSINE_RATIO_MAX = 0.97`: Tỷ lệ top-2/top-1

## Lý do
1. **COSINE_MIN = 0.55**: Thử nghiệm với ~50 sinh viên, threshold 0.55 cho false positive rate < 1%. Threshold 0.50 cho quá nhiều false positive với ảnh chụp góc nghiêng
2. **GAP = 0.03**: Đảm bảo top-1 "thật sự" tốt hơn top-2. Nếu hai người giống nhau (ví dụ: anh em sinh đôi), gap < 0.03 → reject
3. **RATIO = 0.97**: Bổ sung cho gap — nếu top-1 = 0.60, top-2 = 0.59 thì gap = 0.01 < 0.03 → reject (đúng). Nhưng nếu top-1 = 0.90, top-2 = 0.87 thì gap = 0.03 ≥ 0.03, ratio = 0.967 ≤ 0.97 → accept (hợp lý vì confidence cao)

## Hệ quả
- Cần tune lại nếu số lượng sinh viên > 200 (nhiều người giống nhau hơn)
- Auto-learn feature bật ở 98% match → dần cải thiện accuracy
- Các giá trị có thể override qua ENV vars
