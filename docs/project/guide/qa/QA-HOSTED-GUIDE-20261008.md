# Kiểm đóng gói hướng dẫn web — 08/10/2026

Người dùng báo `/huong-dan/index.html` bị nhận diện `HEUR:Trojan.Script.Generic`. Chưa có tên/phiên bản antivirus, URL đầy đủ, bytes của phản hồi máy chủ hoặc log phát hiện. Chưa kết luận mã độc, cảnh báo nhầm hay nguyên nhân chính xác.

## Phạm vi và phát hiện

- Chỉ thay cơ chế đóng gói hướng dẫn: `../tdtd/tdtd-fe/scripts/sync-user-guide.mjs`, phần hướng dẫn trong README, `public/huong-dan/` và metadata sinh tự động `src/generated/userGuide.ts`.
- Thêm kiểm hồi quy `tools/check-hosted-guide.mjs` trong dự án hướng dẫn. Không thay nội dung bài học, source HTML offline, component FE/BE, dữ liệu UAT hoặc quyền truy cập. Không build/run/restart sản phẩm, không triển khai máy chủ.
- Bản web cũ có 3 loader giải mã Base64 thành HTML và gán `iframe.srcdoc`, dù các nhánh đó không cần dùng vì bài thử đã có URL asset. HTML còn 4 tệp XLSX dạng data URL và JavaScript inline. Đây là điểm cần làm rõ trong gói phát hành, chưa phải bằng chứng xác định nguyên nhân antivirus.
- Rà riêng HTML và script chính của trang không thấy `eval`/`new Function`. Các bundle demo vẫn chứa `new Function`, mã network/storage hoặc URL tài liệu từ thư viện bên thứ ba; các bundle này được giữ nguyên bytes. Kiểm bằng regex không chứng minh toàn bộ mã an toàn. Không tải file hoặc ảnh lên dịch vụ quét ngoài.

## Thay đổi

- Bản web chỉ mở bài mẫu qua `iframe.src`; không đóng gói fallback `atob`/`srcdoc` của bản HTML offline.
- JavaScript, CSS và 4 XLSX được tách thành file tĩnh có tên từ SHA-256. Ba chỗ giữ URL demo chuyển thành template inert, giữ nguyên ID để không đổi handler.
- CSP trên trang chính chỉ cho script cùng nguồn, không cho script inline hoặc eval, không cho kết nối dữ liệu. Iframe giữ nguyên sandbox và CSP riêng của demo.
- Lệnh đồng bộ dừng nếu loader nguồn đổi cấu trúc hoặc script chính còn giải mã HTML/thực thi động. `--check` kiểm thêm ranh giới HTML/script và hash từng asset.
- HTML web: 888,201 → 750,953 bytes. Manifest: 163 → 170 assets. Asset demo và ảnh hiện có được giữ nguyên; không xóa file cũ ngoài manifest do có WIP dùng chung.

## Bằng chứng

| Kiểm | Kết quả |
|---|---|
| `node --check scripts/sync-user-guide.mjs` tại FE | PASS |
| `node scripts/sync-user-guide.mjs` | PASS, sinh riêng gói tài liệu |
| `node scripts/sync-user-guide.mjs --check` | PASS, HTML/metadata/170 asset và source sibling khớp |
| `node tools/check-guide-browser.mjs` | PASS ở 1440×900 và 390×844: 2 trình xem luồng, không tràn ngang, nội dung giữ trong print media |
| `node tools/check-hosted-guide.mjs` | PASS 4 tổ hợp: HTTP/FILE × desktop/mobile |
| Kiểm 4 tổ hợp | Neo/ID tĩnh giữ nguyên, script sinh ra parse được, demo chỉ tải khi mở, đồng bộ cấp nhập thử, UI biểu mẫu mount, báo cáo dừng/tiếp/mở lại giữ cảnh, liên kết XLSX hợp lệ, không tràn ngang |
| Console/network trong các ca iframe trên | Không lỗi JavaScript/CSP hoặc request ngoài tài nguyên hướng dẫn; server QA xử lý favicon bằng 204 |
| `git diff --check -- README.md` tại FE | PASS |

Các lần đầu của công cụ hồi quy cần sửa phép so sánh ID (bỏ chuỗi HTML bên trong script), locator chọn cấp bị trùng, nhãn nút Tiếp, điều kiện UI mount và xử lý favicon của server QA. Kết quả PASS là lượt cuối, không dùng kết quả lỗi harness để kết luận sản phẩm lỗi.

HTML web SHA-256: `6a17a46173825c4cd3740fd203f9c3cf2570df256261ce0d4ef8f5b71746fbe8`.

Source HTML offline SHA-256 giữ nguyên: `3519c1470200951916c64060a1ad9bb3d47ac58f9e2ec76df33dafed85c67b5b`.

Link metadata mới: `/huong-dan/index.html?v=6a17a46173825c4c`.

## Bàn giao và phần chưa xác nhận

Gói `output/huong-dan-web-20261008.zip` chỉ chứa `huong-dan/index.html`, `huong-dan/manifest.json` và 170 asset hiện hành. Cập nhật cả cây thư mục khi triển khai; thay riêng index.html sẽ thiếu JS/CSS mới. Mã nội dung cho biểu tượng sách nằm trong metadata FE; chỉ triển khai thư mục tĩnh chưa đổi mã link trong bundle FE đang chạy.

Chưa triển khai máy chủ, chưa so bytes bản online với workspace, chưa quét bằng antivirus báo lỗi hoặc gửi hãng phân tích. Chưa xác nhận hết cảnh báo. Chưa chạy toàn bộ autoplay 20 cảnh ở lượt này, chỉ kiểm loader và điều khiển nêu trên. Chưa xuất/so PDF mới, chưa build/test toàn sản phẩm. GUIDE_QA không thay verdict LIVE_UAT hiện có.
