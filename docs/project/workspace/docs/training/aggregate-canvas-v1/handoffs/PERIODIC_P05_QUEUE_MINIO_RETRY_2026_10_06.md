# P05 — lỗi gửi job và retry snapshot MinIO

Ngày 06/10/2026. Lam tiếp tục theo yêu cầu trực tiếp “Tiếp tục”, sau nhóm source race/Hangfire Server recovery. Chỉ kiểm cơ chế AS-J1 hiện hành; không mở cache, versioning/đổi mẫu, so sánh kỳ, Table engine hoặc P06. Giữ WIP và FE/BE dùng chung.

## Cách kiểm và giới hạn bằng chứng

- Fixture mới có nhãn `r8-text-20261005-<run>-monthly`, hai report Draft thuộc cùng Work riêng. Tái dùng seed đã publish Form trước khi pin, normal Program Development + migration + login/API thật và authoritative payload writer.
- **Lỗi hàng đợi:** sau khi HTTP đã lưu phương pháp/intent, chờ reservation gửi ban đầu hết tự nhiên; chèn lỗi tại `IBackgroundJobClient.Create` trong dispatcher chỉ giới hạn Work fixture. Kiểm intent PENDING, chưa claim, không mất nguồn/cấu hình, không đổi giá trị cũ; gọi lặp không tạo vòng retry nóng. Nộp qua API phải bị chặn khi chưa có kết quả mới. Chờ reservation lỗi hết tự nhiên, gọi host dispatcher sản phẩm với Work fixture và để Hangfire xử lý job thật.
- **Lỗi MinIO:** report thứ hai tính/ghi trước, intent snapshot còn PENDING. Chèn lỗi đúng một lần ở `IMinioClient.PutObjectAsync` của job snapshot riêng. Dùng Hangfire thật và AutomaticRetry của job sản phẩm. Kiểm không giả READY, report không bị ghi lại, API page/part vẫn đọc đúng nội dung dài; khi retry thành công, tải file từ MinIO thật và đối chiếu SHA256. Giao trùng job không thay metadata hoặc payload.
- So frozen history trước/sau (có kiểm số snapshot lịch sử > 0 để tránh kiểm trên tập rỗng), audit provenance sau ca. Không sửa lease/reservation hoặc trạng thái job trong DB để rút thời gian chờ.
- Đây là **fault injection tại client outbound + real runtime phía còn lại**. Không dừng Mongo/MinIO, không ngắt mạng toàn dịch vụ, không nhận đã kiểm mất điện/network partition thực hoặc recurring scheduler tự thức. Dispatcher retry được harness gọi tường minh khi reservation hết. Không phải browser/Yud UAT hoặc tải 166 × 200 mới.

## Source được bổ sung

Owner Aggregate Lam; đường dẫn từ `tdtd-be/`:

| File | Thay đổi |
| --- | --- |
| `tests/AggregateTextBlocksRuntimeChecks/TextMaterializationOutageChecks.cs` | Ca queue failure/recovery, native pending Submit guard, MinIO failure/automatic retry/readback/duplicate và frozen history. Prefix job riêng theo run. |
| `tests/AggregateTextBlocksRuntimeChecks/TextPeriodicLifecycleChecks.cs` | Gắn opt-in sau source/formula checks bằng `AGG_P05_OUTAGE_CHECKS=1`. |
| `tests/AggregateTextBlocksRuntimeChecks/Program.cs` | Harness outage được tối đa 15 phút để chờ reservation thật; không đổi timeout sản phẩm. |
| `tests/AggregateTextBlocksRuntimeChecks/AggregateTextBlocksRuntimeChecks.csproj` | Link lại `MinioUploadFault.cs` hiện hữu, không tạo evaluator/storage/quyền giả. |

Không sửa file sản phẩm, appsettings, FE, master/issue board. Baseline WIP: `outputs/aggregate-operators-20261005/p05-outage-{be,fe}-status-before.txt`.

## Bằng chứng chạy

**240 API/job/writer assertion PASS**, gồm **24 assertion mới**. Process kết thúc exit 0. Không cộng 239 assertion của lượt recovery trước vào lượt này; các ca nền lặp lại. Log `outputs/aggregate-operators-20261005/p05-outage-runtime.log`, manifest `p05-outage-bin/p05-outage-r8-text-20261005-2b45e13f.json`.

Build harness đã qua (`p05-outage-build-verified.log`: incremental 0 errors/0 warnings); không nhận warning toàn BE đã hết. Hai lượt đầu thiếu namespace DTO Submit, giữ log lỗi và sửa import đúng `tdtd_be.DTOs.WorkAssignmentReports`, không sửa DTO sản phẩm. Hash bốn file harness và hai DLL trong `p05-outage-artifact-hashes.json`; whitespace bốn file qua.

| Ca | Kết quả thật |
| --- | --- |
| Lưu phương pháp trước khi job chạy | HTTP ack **54/55 ms** trên hai fixture nhỏ; giá trị report chưa bị ghi khi lưu intent. Đây không phải INP hoặc tải 166 × 200. |
| Upload snapshot lỗi một lần | Intent không giả READY; report giữ PayloadRevision/Hash; API page và part của text >1.000 ký tự vẫn trả đúng dữ liệu trước lỗi. |
| MinIO AutomaticRetry | Hangfire có lịch sử Scheduled sau lỗi, rồi Succeeded; đúng một intent cho revision; file MinIO 3.771 byte tải lại khớp SHA256. Khoảng **16.817 ms** từ bắt đầu ca tới retry/readback/duplicate, gồm delay retry. |
| Giao trùng snapshot job | Metadata snapshot và payload report không đổi. |
| Gửi job tính thất bại | Dispatcher thực sự gặp lỗi khi gửi đúng instance/generation; intent PENDING, attempt 0, không có lease worker; giữ cấu hình/lựa chọn nguồn và giá trị cũ. |
| Không gửi dồn trong outage | Dispatcher được gọi lại khi reservation còn hạn không gọi queue client thêm lần nào. |
| Nộp khi đang pending | API Nộp trả `AGG_COMPUTATION_REQUIRED`; report vẫn Draft, lifecycle revision nguyên. |
| Queue phục hồi | Chờ reservation hết tự nhiên; `AggregateHostIntegration.DispatchAsync` enqueue lại đúng generation, Hangfire tới Succeeded; payload tăng đúng một revision. Khoảng **121.280 ms** từ lỗi gửi tới hoàn tất, gồm chờ reservation hai phút. |
| Replay command/delivery | Receipt replay và delivery trùng không tính/ghi thêm payload. |
| Frozen/provenance | Frozen history có thật và hash trước/sau không đổi; migration trước/sau seed, startup normal host và sau kiểm qua. Không reset/skip guard. |

Fixture giữ lại để truy vết:

- Run: `r8-text-20261005-2b45e13f`; Work: `6ac47cd4352faa6d97ffe6e8`.
- Report kiểm queue: `6ac47cd4352faa6d97ffe6f1`, instance `788601709C6FE5BF0F5A8F01A6E7F88ABD98CA6850593F814D62ACE0682DFF75`, generation 14.
- Report kiểm MinIO: `6ac47cd4352faa6d97ffe6f7`, instance `AF5C601E783EAFA37631F9D3A8D696A651471521BB6F07E378EDE3A37A49D074`.
- Snapshot `0E328A559D8DD45564060630C0742D32D8440CF0C04A72CB97C3E549855EFD2E`; upload job `6ac47d07352faa6d97ffe8dc`; duplicate job `6ac47d18352faa6d97ffe8e5`.
- SHA256 file: `4F632DD78496F2AE0A044A02AD247821CE0EF49AF00548A8D07F318489755D95`.
- Audit sau kiểm `p05-outage-fixture-audit.log` PASS: ba report đích Draft, khóa nguồn không còn owner, outbox lifecycle đã Completed. Normal lifecycle được kiểm trên target và một source; các nguồn còn lại seed Approved, không gọi tất cả là browser nhập/nộp thật.

Readiness shared BE trước/sau: `p05-outage-readiness-before.json`, `p05-outage-readiness-after.json`, đều **200 Healthy**. Sau kiểm BE 5164 PID 77180, FE 5173/5187/5191 có listener. Không restart dịch vụ dùng chung; private host/server của harness đã kết thúc cùng process.

Lệnh từ `tdtd-be/`, chỉ tạo fixture riêng mới:

```powershell
$env:AGG_P05_OUTAGE_CHECKS='1'
$env:AGG_P05_MATERIALIZATION_RECOVERY='0'
dotnet ../outputs/aggregate-operators-20261005/p05-outage-bin/tdtd-be.IntegrationTests.dll --database tdtd --run-own-text-fixture --with-periodic-lifecycle
```

## Bước tiếp

Sau các ca này, còn tải dài kỳ/nhiều job, browser Submit/Return đa vai trò và tuần giao tháng, INP toàn canvas. Giữ riêng gap outage mạng/service toàn phần/recurring scheduler; chỉ gọi đã khép các ca quan sát được. P05 chưa đóng, P06 chưa mở. Không đưa các mở rộng Yud đã hoãn trở lại điều kiện nghiệm thu.
