# Hướng dẫn chuyển License từ GitHub sang Cloudflare Pages

## Tại sao chuyển?
- GitHub Raw từ VN: 200-800ms, giới hạn 60 request/giờ
- Cloudflare Pages từ VN: 20-80ms (edge ở HCM), không giới hạn request

---

## Bước 1: Tạo tài khoản Cloudflare (miễn phí)

1. Vào https://dash.cloudflare.com/sign-up
2. Đăng ký bằng email → xác nhận email → đăng nhập

---

## Bước 2: Chuẩn bị file license

Tạo 1 thư mục trên máy, ví dụ: `C:\license-site\`

Trong đó tạo 2 file:

### File 1: `index.html` (bắt buộc phải có)
```html
<!DOCTYPE html>
<html><body><p>License Server</p></body></html>
```

### File 2: `license.txt` (file license của bạn)
```
# License Tool_Struct
# Format: PC_NAME;MAC_ADDRESS
PC01;001122AABBCC
PC02;00-11-22-AA-BB-CC,112233445566
```

Cấu trúc thư mục:
```
C:\license-site\
  ├── index.html
  └── license.txt
```

---

## Bước 3: Deploy lên Cloudflare Pages

### Cách 1: Upload trực tiếp (đơn giản nhất)

1. Đăng nhập https://dash.cloudflare.com
2. Menu bên trái → **Workers & Pages**
3. Click **Create** → chọn tab **Pages** → **Upload assets**
4. Đặt tên project: `tool-struct-lic` (hoặc tên bạn muốn)
5. Click **Upload** → chọn thư mục `C:\license-site\` hoặc kéo thả 2 file vào
6. Click **Deploy site**
7. Xong! Bạn sẽ nhận được URL dạng:
   ```
   https://tool-struct-lic.pages.dev
   ```
8. File license của bạn sẽ ở:
   ```
   https://tool-struct-lic.pages.dev/license.txt
   ```

### Cách 2: Kết nối GitHub (tự động cập nhật)

1. Tạo repo mới trên GitHub (public hoặc private đều được)
2. Push 2 file `index.html` và `license.txt` lên repo
3. Trong Cloudflare Dashboard → **Workers & Pages** → **Create**
4. Chọn tab **Pages** → **Connect to Git**
5. Chọn repo GitHub vừa tạo
6. Build settings:
   - Framework: `None`
   - Build command: (để trống)
   - Build output directory: `/`
7. Click **Save and Deploy**
8. Từ giờ mỗi khi bạn push thay đổi license.txt lên GitHub, Cloudflare sẽ tự động deploy

---

## Bước 4: Kiểm tra

Mở trình duyệt, vào URL:
```
https://tool-struct-lic.pages.dev/license.txt
```

Nếu thấy nội dung file license → thành công!

---

## Bước 5: Cập nhật code C#

Chỉ cần đổi 1 dòng trong `LicenseConfig`:

```csharp
// CŨ (GitHub Raw)
public const string LicenseUrl =
    "https://raw.githubusercontent.com/nguyendinhthi/LIC/refs/heads/master/license.txt";

// MỚI (Cloudflare Pages)
public const string LicenseUrl =
    "https://tool-struct-lic.pages.dev/license.txt";
```

---

## Cập nhật license sau này

### Nếu dùng Cách 1 (Upload trực tiếp):
1. Vào Cloudflare Dashboard → Workers & Pages → chọn project
2. Click **Create new deployment**
3. Upload lại thư mục với file `license.txt` đã cập nhật
4. Click **Deploy**

### Nếu dùng Cách 2 (GitHub):
1. Sửa file `license.txt` trên GitHub
2. Commit & Push
3. Cloudflare tự động deploy trong vài giây

---

## Lưu ý quan trọng

- URL Cloudflare Pages luôn dùng HTTPS (tự động, miễn phí)
- Không cần cấu hình CORS hay header gì thêm
- Free plan: unlimited requests, 500 deployments/tháng
- Nếu muốn dùng domain riêng (ví dụ: lic.yourcompany.com), vào project → Custom Domains
