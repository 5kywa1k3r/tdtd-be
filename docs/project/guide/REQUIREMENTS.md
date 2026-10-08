# Requirements — Hướng dẫn HTML TD-TD
Baseline v0.6.0 · 06/10/2026; các yêu cầu và bằng chứng phiên bản trước giữ để theo dõi thay đổi.

## Phạm vi và đối tượng
Người đọc phần I: người dùng (cán bộ, chỉ huy, lãnh đạo) và tài khoản quản trị. Phần II–III dành riêng cho tài khoản quản trị cấp phòng và công an phường/xã. Phần IV dành cho cán bộ được phân công và có quyền thiết kế biểu mẫu.
Nội dung: đăng nhập, tự đổi mật khẩu/đăng xuất, quản lý đơn vị con, tài khoản, nhập tệp; tạo/lưu nháp và Nhập thử biểu mẫu động.
Không hướng dẫn quản lý danh mục loại đơn vị/chức vụ hoặc tạo quyền quản trị hệ thống.
Không sửa TD-TD, không kết nối dữ liệu thật để chạy minh họa, không coi ảnh mẫu là UAT.

## Yêu cầu nghiệp vụ
| ID | Yêu cầu bắt buộc | Nguồn/kiểm chứng |
|---|---|---|
| B01 | Dùng tên đăng nhập được cấp; nhãn FE hiện là Email / Tên đăng nhập, chưa suy ra email login | LoginPage + UAT-A01 |
| B02 | tài khoản quản trị vào menu tài khoản → Quản trị tài khoản; hai tab Đơn vị, Người dùng | Header + AdminAccountPages |
| B03 | tài khoản quản trị phòng tạo Đội; tài khoản quản trị phường/xã tạo Tổ trực thuộc. Tổ không có cấp dưới | FE/BE AccountAdministrationRules + UAT-U01/U02 |
| B04 | Kiểm đơn vị cha trước khi lưu; mã đơn vị do BE cấp; ký hiệu không trùng | UnitService + UAT-U01/U02 |
| B05 | Đơn vị ảo chỉ gom nhóm, không tạo tài khoản trực tiếp | UnitEditorDrawer/UserEditorDialog + UAT-U03 |
| B06 | Người dùng thuộc đơn vị mình hoặc nhánh được phép; chức vụ theo đơn vị đích; đổi đơn vị thì chọn lại chức vụ | UserEditorDialog + UAT-N01/N02 |
| B07 | Tạo người dùng không tự cấp quyền tài khoản quản trị; không mô tả role picker đang ẩn | UserEditorDialog + UAT-N03 |
| B08 | Sửa người dùng: tên đăng nhập, họ tên, chức vụ; không đổi đơn vị hoặc mật khẩu tại form sửa | UserEditorDialog + UAT-N04 |
| B09 | Nút đặt lại mật khẩu trên danh sách đang dùng hộp xác nhận về mật khẩu mặc định; không dùng ResetPasswordDialog chưa được handler mở làm căn cứ | UsersPanel.handleResetPassword + UAT-N05 |
| B10 | Tự đổi mật khẩu cần mật khẩu hiện tại; mới 6–128 ký tự, khác cũ, xác nhận khớp; thành công đăng nhập lại | ChangePasswordPanel + UAT-A02 |
| B11 | Ngừng dùng người dùng qua xác nhận, không gọi là chuyển giao công việc | UsersPanel + UAT-N06 |
| B12 | tài khoản quản trị không sửa/ngừng dùng đơn vị gốc mình; ngừng dùng con ảnh hưởng cây con và tài khoản quản trị của cây; chặn khi còn người dùng thường đang hoạt động | UnitService + UAT-U04 |
| B13 | Nhập tệp dùng mẫu XLSX/CSV đúng tab; kiểm tra trước, chỉ xác nhận khi không còn dòng lỗi; tra lại sau nhập | ImportDataMenu/UsersPanel/UnitsPanel + UAT-I01 |
| B14 | Chức vụ dùng danh mục hiện hành; Tổ theo cấu hình hiện có chỉ có Tổ trưởng, không tự thêm Tổ phó | Catalog + UAT-N02 |

## Yêu cầu tài liệu và trải nghiệm
| ID | Tiêu chí chấp nhận |
|---|---|
| D01 | Một bài có ID ổn định, tiêu đề theo việc cần làm, đối tượng, điều kiện, các bước, kết quả, lưu ý, nguồn |
| D02 | Có mục lục liên kết sâu; tìm kiếm không phân biệt dấu; trạng thái không tìm thấy rõ ràng |
| D03 | Chọn phường/xã hoặc phòng thay ảnh và chỉ dẫn liên quan; nội dung chung vẫn đọc được |
| D04 | Ảnh bám FE hiện hành, dữ liệu mẫu và mật khẩu che; ghi rõ nguồn ảnh, ngày, phạm vi |
| D05 | Có phóng to ảnh và xem từng bước; trước/sau, phát/dừng, tiến độ, phát lại; không tự phát khi mở bài |
| D06 | Minh họa chạy từ ảnh và chú thích, không gọi API nghiệp vụ; không giả vờ lưu thật |
| D07 | Nội dung đọc không phụ thuộc trình phát; khi JS tắt vẫn đọc/in được nội dung cơ bản |
| D08 | Điều khiển dùng bàn phím, nhãn rõ; dialog giữ focus, Escape đóng; giảm chuyển động khi người dùng yêu cầu |
| D09 | Desktop 1440px và mobile 390px không tràn ngang toàn trang; hình có thể mở lớn |
| D10 | In toàn chương hoặc từng bài; bỏ menu/nút; mở đủ bước; ảnh tĩnh thay minh họa động; A4 không cắt chữ |
| D11 | Bản in ghi chương, phiên bản/ngày và nhánh áp dụng; không tự đánh dấu UAT đã qua |
| D12 | Không có mật khẩu thật, token, danh sách người thật trong nội dung/ảnh; ví dụ có dấu mẫu |

## Yêu cầu kiến trúc mẫu cho agent
- A01: Project độc lập, không nằm dưới source/docs/output của TD-TD.
- A02: Source nội dung JSON tách khỏi trình bày; source CSS/JS tách file; build sinh một index.html tự chứa.
- A03: Mở file trực tiếp được; bản phân phối không dùng thư viện ngoài/CDN/API; không cần FE chạy.
- A04: Công cụ tạo ảnh được phép đọc FE, nhưng là công cụ phát triển tùy chọn; không thành phụ thuộc runtime.
- A05: Các ảnh có manifest nguồn và SHA-256 source được dùng khi chụp, ghi nhận source dirty.
- A06: Không sao chép logic nghiệp vụ BE thành một app thứ hai; dùng kịch bản minh họa cố định.
- A07: Thêm chương theo schema và đăng ký rõ ràng; không dùng sửa tay index.html làm quy trình.
- A08: Có README, AGENTS, template bài/chương và ma trận UAT để agent học theo.
- A09: Tích hợp vào TD-TD là việc riêng: có thể phục vụ tệp tĩnh dưới đường dẫn help, không tự sửa route/menu trong lượt này.
- A10: Dùng Node chuẩn cho build/preview/check; không cần cài package để đọc hoặc build HTML.

## Kiểm chứng và định nghĩa hoàn thành
- E01: Source review = đối chiếu source; không phải runtime.
- E02: FE minh họa = render component thực, dữ liệu/API giả, chặn network ghi thật.
- E03: QA tài liệu = kiểm HTML, tương tác, responsive, ảnh, print.
- E04: UAT thực = tài khoản/môi trường thật được cho phép, có kết quả API/DB/đăng nhập phù hợp ca.
- E05: Một bài có thể hoàn tất biên soạn trong khi UAT thực còn chờ. Hai trạng thái phải ghi riêng.
- E06: Không chạy tạo/ngừng dùng/đặt lại mật khẩu trên dữ liệu thật chỉ để lấy ảnh.
- E07: Hoàn thành mẫu khi D01–D12/A01–A10 có bằng chứng phù hợp, và khoảng trống UAT được công khai.
## Bổ sung v0.2.0 — Q&A và mocktest (05/10/2026)
Yud yêu cầu nâng tìm kiếm thành hỏi–đáp không AI và cho thao tác thử với bộ mẫu theo cấp đơn vị.
Quy tắc mới này thay phần giới hạn chỉ dùng slideshow của D06; A06 vẫn cấm sao chép cả ứng dụng nghiệp vụ, nhưng cho phép mô hình tương tác nhỏ cho các bài login/unit/user.
Ảnh chụp component FE vẫn là lớp đối chiếu. Form thực hành là HTML viết riêng bám nhãn/luồng FE và dùng dữ liệu hư cấu; không phải bản FE sản phẩm đang chạy.

| ID | Tiêu chí bổ sung |
|---|---|
| Q01 | Q&A biên soạn sẵn trong questions.json; tìm theo từ khóa có/không dấu, mk/user/login và từ đồng nghĩa; không gọi AI |
| Q02 | Trả tối đa 5 câu liên quan, mở câu đầu khi tìm; có bộ chọn toàn bộ câu hỏi có sẵn; câu chưa có đáp án phải nói rõ; không lọc mất bài hướng dẫn |
| Q03 | Mỗi đáp án trỏ bài gốc; câu phù hợp có nút thực hành; đổi vai trò cập nhật ngữ cảnh |
| M01 | Ba bài độc lập: đăng nhập, tạo đơn vị con, cấp tài khoản; tự thao tác hoặc bấm tự chạy |
| M02 | Bộ xã tạo TO; bộ phòng tạo DOI; cây đơn vị, tài khoản và chức vụ đổi theo bộ được chọn |
| M03 | TO chỉ có TO_TRUONG; DOI có chức vụ mẫu phù hợp. Đây là tập danh mục minh họa, không tuyên bố đầy đủ danh mục triển khai |
| M04 | Kiểm thiếu dữ liệu, trùng tên đăng nhập/ký hiệu, sai loại/cha, đơn vị ảo/phạm vi, chức vụ; lỗi giữ bản nhập |
| M05 | Đổi đơn vị đích xóa chức vụ đã chọn; tạo thành công hiện bảng đối chiếu đơn vị/cha/chức vụ |
| M06 | Mở/đổi bộ/làm lại có dữ liệu mới; chỉ lưu trong bộ nhớ; không API, DB, storage, token hoặc tài khoản thật |
| M07 | Đóng/ẩn tab/tự thao tác dừng tự chạy; dialog dùng bàn phím, Escape; form dùng được trên màn nhỏ |
| M08 | Q&A và mocktest không in; các bài và ảnh tĩnh tiếp tục là nội dung bản in |
| E08 | GUIDE_MOCK_QA chỉ xác nhận tài liệu và bộ mẫu; không thay LIVE_UAT_PASSED |

## Yêu cầu 0.3.0 đã chốt với Yud
- D13: Hai phần lớn I. Giao diện đăng nhập và II. Giao diện quản trị (dành riêng cho tài khoản quản trị).
- D14: Giữ hai nhóm Quản trị tài khoản / Quản trị người dùng. Quản trị tài khoản gồm đơn vị và tài khoản người dùng; nhóm Quản trị người dùng hướng dẫn thao tác chi tiết với cán bộ/chỉ huy/lãnh đạo. Nhãn tab thực vẫn Đơn vị / Người dùng.
- D15: Nêu rõ phân hệ Người dùng dành cho cán bộ, chỉ huy, lãnh đạo; phân hệ Quản trị dành cho tài khoản quản trị. Việt hóa cách gọi, không dùng MU trong nội dung hiện ra.
- D16: Bỏ câu mở đầu “Từ lần đăng nhập đầu tiên đến tạo đơn vị con và cấp tài khoản cho cán bộ. Làm theo từng bước, tra cứu khi cần.”
- D17: Bước 3 đăng nhập dùng ảnh mới và focus đúng dòng Quản trị tài khoản; chỉ áp dụng việc mở menu quản trị cho tài khoản quản trị.
- Q04: Bộ chọn có đủ câu hỏi theo mẫu, phân nhóm theo chủ đề; ô tìm chỉ tra từ khóa. Chọn câu hỏi và nhập từ khóa xóa lựa chọn của cách còn lại.
- M09: Bài login có Người dùng/Quản trị, kết thúc ngay sau đăng nhập thành công; không gọi tạo đơn vị/người dùng. Bài unit/user độc lập, giữ đúng giới hạn từng bài.

## Yêu cầu 0.4.0 — mẫu tệp và FE nhập thử
Yud cho phép sửa bố cục FE Người dùng khi xác nhận lỗi ở source gốc, rồi chụp lại. Đây là ngoại lệ có phạm vi cho giới hạn không sửa TD-TD trước đó; không mở quyền sửa BE hay dữ liệu thật.

| ID | Tiêu chí bổ sung |
|---|---|
| I01 | Giải thích mẫu XLSX/CSV, trang dữ liệu đầu tiên, trang tra mã, đúng tên và thứ tự 11 cột Đơn vị / 7 cột Người dùng |
| I02 | Có bảng quy ước cột, trường bắt buộc, mã cha/đích, quantity=1, expectedCode tùy chọn, roles trống, mã giữ số 0 đầu |
| I03 | Ví dụ riêng phường/xã và phòng; TO/DOI, TO_TRUONG/DOI_TRUONG; dùng dữ liệu hư cấu và mật khẩu mẫu riêng |
| I04 | Tải mẫu XLSX/CSV có thể mở lại và chọn nhập; có cả tệp hợp lệ và tệp cố ý sai, không dùng tải giả hoặc nhập bằng nút tự thành công |
| I05 | Menu tải/nhập và hộp kết quả dùng component của FE sản phẩm, có manifest hash; adapter dữ liệu chạy riêng trong bộ nhớ |
| I06 | Nhập thử thực sự đọc tệp: báo tổng/hợp lệ/lỗi, dòng/cột; còn lỗi khóa xác nhận, đóng giữ danh sách cũ, sửa rồi chọn lại được |
| I07 | Xác nhận chỉ thêm vào danh sách mẫu; kiểm trùng sau nhập; đổi cấp/tab hoặc làm lại đưa về bộ dữ liệu ban đầu; không API/storage |
| I08 | Runtime bài thử đóng gói offline, iframe chỉ cho script và download; normal build không cài dependency; bảng cột/ví dụ vẫn đọc và in được |
| I09 | Kiểm bố cục Người dùng tại các kích thước; nếu lỗi FE có thật, sửa FE rồi mới dùng ảnh chụp mới. Không sửa ảnh để che lỗi nguồn |
| I10 | Nội dung nhắc thay/xóa ví dụ trong mẫu thật, chọn đúng chức vụ theo đơn vị, tra lại sau lỗi xác nhận; không hứa nhập có giao dịch hoàn tác |

A03/A10 tiếp tục áp dụng cho tệp phân phối và build tài liệu. Công cụ tái tạo runtime React/XLSX là tùy chọn, dùng thư viện sẵn có từ FE gốc. Không bắt người đọc cài FE.

## Quyết định trực tiếp 0.5.0 — ba phần cơ bản và tra mã nhập tệp
- D18: Theo yêu cầu mới, ba phần chính là I. Đăng nhập, II. Quản trị đơn vị, III. Quản trị tài khoản; thay cấu trúc hai phần/hai nhóm D13–D14. Nhãn sản phẩm vẫn Quản trị tài khoản → Đơn vị / Người dùng. Giữ ID bài và alias liên kết cũ.
- B15: Phân biệt tài khoản quản trị với tài khoản cá nhân của cán bộ/chỉ huy/lãnh đạo. Chức vụ lãnh đạo không tự cấp quyền quản trị. Ví dụ username lấy gói kiểm thử, không lấy tên làm quyền.
- B16: Mã đơn vị trên user lấy đơn vị công tác đã chọn. Trưởng phòng ở phòng dùng mã phòng; đội trưởng/cán bộ ở Đội dùng mã Đội; cán bộ phường có thể dùng mã phường. Không suy từ username/chức vụ.
- B17: Ví dụ cây phòng → Đội → Tổ và phường/xã → Tổ; quyền tạo cấp con còn theo loại đơn vị của tài khoản quản trị. Tổ leaf, danh mục Tổ hiện chỉ có Tổ trưởng.
- I11: Ảnh từng bước nhập tệp phải chỉ đúng nút Nhập dữ liệu, dòng tải XLSX/CSV và dòng nhập XLSX/CSV; bước mở/điền bảng có ảnh nội dung XLSX mẫu tương ứng. Không dùng ảnh preview thay cho bước chọn tệp.
- I12: Có bảng tra mã đơn vị/chức vụ với code, tên và loại đơn vị của đúng bộ tải thử; hướng dẫn lấy mã triển khai từ Units/Positions trong XLSX tải mới. Positions là danh mục chức vụ đang dùng, không đảm bảo mọi mã được phép cho mọi đơn vị. CSV không có các trang tra mã.
- E09: Danh sách UAT ngắn dùng pv01/cap_hacthanh và các tài khoản cá nhân cùng gói. Ghi source seed đã đối chiếu, chưa khẳng định DB đang chạy có dữ liệu đó; không thực thi UAT/seed trong đợt biên soạn.

## Bổ sung 0.5.1 — tệp danh mục tải riêng (06/10/2026)
- I13: Cho tải riêng XLSX danh mục mã đơn vị và XLSX danh mục mã chức vụ, mỗi cấp phường/xã hoặc phòng có bộ tương ứng. Chỉ chứa các cột danh mục, không kèm Users/mật khẩu. Mã đơn vị giữ dạng chuỗi và số 0 đầu.
- I14: Nút tải ở đầu bài nhập danh sách và có liên kết mục lục; đổi cấp đồng bộ với bộ thử. Bytes tệp được nhúng trong HTML tự chứa, tải không cần mạng hoặc runtime nhập thử.
- I15: Nội dung danh mục riêng khớp Units/Positions của XLSX mẫu tương ứng; ghi dữ liệu mẫu. Không trình bày các mã mẫu là danh mục DB hiện hành.
- E10: Ghi hash nguồn/kết quả, kiểm XML xuất và ảnh render. Kiểm bytes/DOM không thay kiểm sự kiện tải bằng trình duyệt hay mở bằng Microsoft Excel.

## Yêu cầu v0.6.0 — một guide chung (06/10/2026)

| ID | Tiêu chí bổ sung |
|---|---|
| D19 | Một index.html, một menu, 4 phần và 15 bài: Đăng nhập / Quản trị đơn vị / Quản trị tài khoản / Tạo biểu mẫu động; không tách guide biểu mẫu sang trang riêng |
| D20 | Giữ neo bài cơ bản; phần biểu mẫu dùng #bieu-mau-dong và ID bài bieu-mau-{id}; liên kết chuẩn http://127.0.0.1:5187/#bieu-mau-dong |
| D21 | Bản public dành cho cán bộ; không hiện tên người giao/duyệt, prompt, handoff, trạng thái nghiệm thu hoặc hướng dẫn điều phối agent. Lưu nguồn/bằng chứng nội bộ ở qa |
| D22 | Sáu bài biểu mẫu đều có thao tác, kết quả cần thấy và điểm dễ nhầm; 9 ảnh fixture minh họa đúng nội dung nhìn thấy, có zoom/player và ảnh bổ sung |
| A11 | content/dynamic-form-guide.json là dữ liệu nội dung; tools/render-dynamic-form-guide.mjs nối vào build; không sao chép cả trang HTML cũ hoặc sửa tay index.html |
| A12 | assets/form-practice.html giữ runtime FE đã đóng gói; build nhúng offline, iframe sandbox allow-scripts. Ảnh assets/dynamic-form/ nhúng inline; normal build/preview/check dùng Node chuẩn |
| A13 | Nguồn và handoff tại docs/training/tap-huan-3-gio/ được giữ làm lịch sử; không sửa bản biểu mẫu đã công bố hoặc dùng ảnh mẫu mới để khẳng định đã xử lý STT thừa |
| Q05 | Một #hoi-dap với 2 nhóm: 33 câu đăng nhập/quản trị, 10 câu biểu mẫu; chọn câu và tìm từ khóa chỉ trong nhóm hiện tại; hai nút xóa riêng và từ khóa gợi ý theo nhóm |
| Q06 | Hỏi–đáp vẫn biên soạn sẵn, tìm có/không dấu, tối đa 5 câu theo từ khóa; có liên kết đúng bài và mở đúng bài thử; câu cơ bản giữ ngữ cảnh theo cấp đơn vị |
| F01 | Dùng Danh sách mô hình/sản phẩm/nhiệm vụ; mỗi phần tử là một mô hình/sản phẩm/nhiệm vụ, có thể thêm phần tử cùng bộ trường |
| F02 | Danh sách gồm đúng 15 trường nghiệp vụ: 13 Văn bản, Thời hạn hoàn thành kiểu Ngày đầy đủ, Dự kiến kinh phí kiểu Số; STT tự động, không thêm trường STT |
| F03 | SĐT dùng Văn bản để giữ số 0 đầu; Đánh giá tiến độ dùng Văn bản. Không tự chốt đơn vị kinh phí, danh mục lựa chọn hoặc trường nghiệp vụ bắt buộc |
| F04 | Một Tài liệu minh chứng kiểu Đính kèm tệp ngoài Danh sách, chung cho cả báo cáo; không đổi thành tên tệp/đường dẫn hay tệp riêng từng phần tử |
| F05 | Phân biệt mã kỹ thuật do hệ thống cấp với Mã nghiệp vụ trong Danh sách. Nhãn FE Nội dung tương ứng Văn bản và hiển thị Văn bản tự do khi xem cấu hình |
| F06 | Bài chỉ hướng dẫn tạo, Lưu biểu mẫu ở Bản nháp và Nhập thử; không hướng dẫn bấm Công bố hoặc suy rộng sang giao việc, nộp–duyệt báo cáo/tổng hợp |
| F07 | Nhãn tài khoản thử thietke_bieumau_mau dùng trong bài thử, không phải tài khoản đăng nhập hệ thống; save mẫu chỉ trong phiên, Trial/chọn tệp không gửi báo cáo hoặc tải tệp lên hệ thống |
| E11 | Browser/print của v0.6 cần bằng chứng riêng; không dùng PASS phiên bản cũ, chương độc lập hoặc DOM mô phỏng để đánh PASS bản ghép hay UAT thật |

D18 tiếp tục áp dụng cho ba phần cơ bản; D19 bổ sung phần IV theo quyết định ghép mới, không thay tên tab hoặc quyền của sản phẩm. Chương trình 180 phút tại bộ tài liệu tập huấn vẫn là đề xuất chờ rà, giữ Tổng hợp 50 phút và nghỉ 10 phút; không đưa trạng thái nội bộ này vào guide public.

## Yêu cầu v0.7 — viết lại chương IV

Các điều kiện sáu bước/10 câu ở D19/D22/Q05 và điểm dừng cũ F06 là lịch sử, được thay riêng cho chương IV bởi phạm vi sau; ba chương cơ bản giữ nguyên.

| ID | Tiêu chí |
|---|---|
| F08 | A: sơ đồ Biểu mẫu → Phần → trường độc lập/Bảng/Danh sách, phân biệt người thiết kế và người nhập, xem cấu trúc hoàn chỉnh trước |
| F09 | B: 10 mục riêng cho Nội dung, Soạn thảo văn bản, Danh sách nội dung, Số, Ngày/kỳ, Ngày đầy đủ, Có/không, Chọn một, Chọn nhiều, Đính kèm tệp; 3 mục riêng Phần/Bảng/Danh sách |
| F10 | Mỗi mục có tình huống, các bước ngắn và kết quả, tùy chọn thật, điểm dễ nhầm, ảnh cấu hình/nhập thử hoặc ghi rõ ảnh thiếu |
| F11 | C: 5 trường chung, 15 nghiệp vụ trong Danh sách/STT tự động, 1 tệp chung; tạo → cấu hình → sắp xếp → lưu → nhập thử → lưu thay đổi → Công bố |
| F12 | Hướng dẫn sao chép cấu trúc, ID/mã mới và Bản nháp; không sửa nguồn hoặc mở cơ chế xin quyền đang xử lý |
| F13 | Không đồng nhất xác nhận hộp cấu hình với lưu, nhập thử với nộp, Công bố với giao việc/báo cáo; điều kiện Công bố theo nguồn đã kiểm |
| F14 | Tổng số mô hình là ô nhập, chưa có tự đối chiếu Danh sách; Tình hình thực hiện chung là lựa chọn mẫu của Form, không trạng thái hệ thống |
| F15 | Không suy mọi kiểu vào List/Table; tên bắt buộc khi thiết kế khác kiểm thiếu khi nộp; overview không tạo phép tổng hợp |
| F16 | Markdown/source JSON, HTML chung/trang chuyển tiếp, QA, iframe và bản in đồng bộ; giữ lịch tập huấn 180/50/10 |
| E12 | Kiểm tài liệu/fixture/source tách khỏi UAT sản phẩm; ảnh công bố thật và consumer Tổng quan thiếu phải bàn giao chính xác |

Nguồn đối chiếu riêng v0.7: qa/FORM-SOURCE-V07.md. Khả năng đã đọc source không tự xác nhận ảnh/runtime hoặc nghiệm thu.

## Điều hướng chương v0.7.1 — 06/10/2026

- N01: Cả bốn chương có điều khiển Thu gọn/Mở rộng tại tiêu đề và mục lục; dùng bàn phím được, trạng thái aria-expanded và aria-controls đúng.
- N02: Mục lục, liên kết trong bài, hỏi–đáp và neo mở trực tiếp tự mở chương đích và nhóm mục lục chứa bài. Giữ các neo cũ.
- N03: Thanh cuộn mục lục ở mép trái; chữ và thứ tự đọc vẫn từ trái sang phải. Màn nhỏ không tràn ngang cả trang.
- N04: Thu gọn không làm mất nội dung bản in; in một bài hoặc chương biểu mẫu giữ đúng phạm vi, kết thúc in giữ trạng thái đọc trước đó.

## Màn quản trị v0.7.2 — 06/10/2026

- C01: Đơn vị: Thêm đơn vị/Nhập dữ liệu trong Đang chọn, trên thông tin đơn vị; khung hẹp cho xuống dòng.
- C02: Người dùng: hàng lọc và nút chung; menu 3 mục, XLSX có danh mục, CSV cần tải thêm XLSX. Users điền dữ liệu, Units/Positions tra mã theo nơi công tác/loại đơn vị.
- C03: Ảnh thật có tệp và đúng định dạng trước khi gắn link; đồng bộ Markdown/HTML/player/chú thích/in.
- C04: Đối chiếu source BE không xác nhận mẫu tải runtime; kiểm sau BE cập nhật giữ trong QA/handoff, không đưa điều phối vào public.

## Menu và đánh số v0.7.3 — 06/10/2026

D25: Menu chương dùng tam giác mở/đóng như nhóm con, không viền hoặc nền hộp bao icon; có focus bàn phím và nhãn truy cập. D26: Thay A/B/C bằng10/11/12 ở các mục lớn Biểu mẫu động; số bài con11.x/12.x đồng bộ menu và tiêu đề nội dung. Giữ neo, hỏi–đáp, trình phát và cơ chế in.

## Chiều rộng menu v0.7.4 — 06/10/2026

D27: Nới menu để tên mục dễ đọc, đồng bộ chiều rộng và lề nội dung ở desktop/tablet/mobile/print. D28: Thanh cuộn menu nằm trái, mảnh3px ở Chromium/IAB; track/nút không gây rối, fallbackthin ở trình duyệt khác. Không tạo thanh cuộn riêng bên phải cho nhóm con.

## In chương và toàn bộ v0.7.5 — 06/10/2026

P01: Bốn nút In chương này chỉ in đúng chương được chọn và metadata tên chương; In toàn bộ lấy cả bốn chương. P02: In đủ nội dung dù đang thu gọn; không thay collapse/ẩn bài/nhóm menu khi kết thúc hoặc hủy. P03: Dừng trình phát, không in menu/QA/iframe/nút; không giữ chọn chương khi chuyển sang in bài riêng hoặc toàn bộ. Kiểm DOM và PDF/browser riêng.

## Tra cứu kiểu dữ liệu v0.7.6 — 06/10/2026

Bỏ hàng chip bên dưới mục11 Tra cứu kiểu dữ liệu và khối thành phần. Điều hướng13 mục qua menu bên trái; giữ nội dung, số mục và neo. Mục12 giữ các liên kết bước thực hành. Sửa renderer, build và kiểm menu/neo trên trình duyệt.

## Bài mẫu dùng chung v0.8.2 — 06/10/2026
Yêu cầu trực tiếp bổ sung một lượt dữ liệu mẫu PV01 tạo/giao → Hạc Thành, Quảng Phú nhập/nộp → PV01 xử lý. Giới hạn một bài offline ở guide; đổi vai giữ trạng thái, có điền mẫu nhanh và reset. Tái dùng component FE gốc, chỉ thay adapter dữ liệu trong bộ nhớ. Không mở quyền ghi sản phẩm/UAT/API/DB. Duyệt thủ công; giữ phân biệt nháp/nộp/duyệt/hoàn thành. Xem tools/report-practice/README.md và qa/QA-V082.md.

## v0.8.3 — xem mẫu tự chạy

Bài Mẫu 1 mở dưới dạng trình xem: tự chuyển vai và điền sẵn nội dung; không yêu cầu cán bộ thực hiện20thao tác. Có Tạm dừng/Tiếp tục, Trước, Tiếp, Xem lại. Khoanh phần cần quan sát bằng DOM component thật, đúng tài khoản/tình trạng từng cảnh. Dừng khi đóng, ẩn tab hoặc in; trở lại giữ cảnh và tạm dừng. Duyệt thủ công và phạm vi mẫu offline giữ nguyên. Không thay kết luận UAT độc lập.

## Mẫu 2 / M01 v0.8.4

| ID | Tiêu chí |
| --- | --- |
| M201 | Một index/menu;11 chức năng nhỏ và7 chặng nối, cùng Q&A địnhsẵn/từkhóa; không simulator mới |
| M202 | PX01 dùng biểu mẫu riêng; chỉ chọn đơnvị qua cây trong đường thao tác được viết; đúng nhánh cha và đại diện đội |
| M203 | Nhập trường đơn/Danh sách/Bảng1D/Bảng2D, dữ liệu thậtM01 ngày04; STT tựđộng,0 khác trống; lưu chưa nộp, nộp chưa duyệt |
| M204 | Bài được giao dừng trước hướng dẫn xácnhận duyệt; nguồn tiếp tục đãduyệt phải ghi đúng hiệntrạng, không đổi verdictMẫu1 |
| M205 | 7 ảnh thật xem trước/copy nguyênbyte/relative, tài khoản và vùng khoanh; thiếuảnhghi rõ, ảnh Nhậpthử/lỗi không làmproofreport |
| M206 | 22:00/tựduyệt hiện tại/M02–M04/hai tầng/hoànthànhwork cònchờ; không suy một nguồn thành toànluồng |
| M207 | GUIDE_QA và LIVE_UAT táchriêng; giữ180phút đềxuất/50Tổnghợp/10nghỉ, WIP/source/allowlist |

Kết quả và giới hạn: qa/QA-V084.md, qa/MAU2-M01-V084-HANDOFF.md.

## Mẫu 2 / M02 v0.8.6 — thay phạm vi cũ bằng quyết định mới

M201/M204/M206 là lịch sử v0.8.4; áp dụng các tiêu chí dưới đây cho bản hiện hành. Không đổi các verdict UAT lịch sử.

| ID | Tiêu chí hiện hành |
| --- | --- |
| M208 | Từng chức năng nhỏ: đúng vai, một thao tác, kết quả cần thấy; điểm dễ nhầm ngắn sau nhóm; nhãn không rõ vào QA |
| M209 | Bài nối M02 12 chặng với dữ liệu đầy đủ tại chỗ, QT01/QT02, M02, hai Bảng và ngày hoàn thành riêng; kết thúc ở Đội 2 Đã duyệt/ba kỳ cần nộp |
| M210 | Markdown và HTML cùng JSON/manifest, một menu, giữ toàn bộ neo M01 và demo Mẫu 1; không simulator M02 |
| M211 | Tám ảnh thật đúng vai/kỳ/trạng thái, copy byte và nguồn/hash; mở lớn/ảnh gốc đọc được; thiếu ảnh cụ thể trong QA |
| M212 | Lưu biểu mẫu khác Công bố; Nhập thử khác report; Lưu nháp khác nộp; ngày triển khai khác hoàn thành; report duyệt khác hoàn thành nhiệm vụ |
| M213 | Hạn 07:00 Việt Nam = 00:00 UTC; auto kinh phí ≥ 0 là cấu hình, quá khứ cần xác nhận, hôm nay còn cần kiểm |
| M214 | PX01 report Nháp một nguồn M01; không coi View hay một nguồn là tổng hợp đủ nguồn/hai tầng; PA02/M03/M04/05–07 chờ bằng chứng |
| M215 | Kiểm guide tách khỏi UAT; thông báo sau tải lại đã có, độ trễ/tự cập nhật chưa chốt; 180 phút chờ rà, 50 Tổng hợp/10 nghỉ |

Hồ sơ v0.8.6: qa/QA-V086.md và qa/MAU2-M02-V086-HANDOFF.md.

## Yêu cầu hiện hành v0.8.7 — ưu tiên hơn phạm vi cũ

- R087-01: hai luồng trong chương V, cùng menu: demo Mẫu 1 từ giao tới trả lại/sửa/nộp lại/duyệt; bài M1 Số hóa hồ sơ cây 1–2–4. Giữ artifact và neo cũ.
- R087-02: Mẫu 2 có 9 nhóm tra cứu dùng chung; khác mô hình đặt trong mục thu gọn. M01/M02 là bài đọc lại đã kiểm, M03/M04 không thành hai walkthrough mới.
- R087-03: vai ở đầu nhóm hoặc khi đổi; thao tác/kết quả ngắn; bảng nhập đủ, không buộc tìm giá trị trong handoff. Tách Lưu/Công bố, Nhập thử/report, Lưu/Nộp và Đã duyệt/hoàn thành nhiệm vụ.
- R087-04: M1 có bốn trường Số; ba SUM và một AVG; nguồn con trực tiếp đã duyệt. Bốn nguồn duyệt, một Nháp, một chờ. Ca lọc 01–29/09 còn cần kiểm; tháng 10 bị loại theo trạng thái chưa đủ chứng minh lọc ngày.
- R087-05: giáo án 180/50/20 chờ duyệt, cùng dữ liệu HTML/Markdown/bản in. Giữ 50 phút Tổng hợp, chuẩn bị phần còn lại trước, mỗi nhóm một báo cáo.
- R087-06: tám ảnh M02 bắt buộc giữ đúng vai/kỳ/trạng thái; thêm ảnh PA02/M1 đúng phạm vi, copy nguyên bytes và manifest. Ảnh thiếu/nhãn chưa xác nhận ở QA. Không dùng mindmap sai hạn hoặc Nhập thử giả report.
- R087-07: QA HTML thật (menu/neo/vai/Q&A/zoom/mobile/in), Node check và hồi quy Mẫu 1. Không đổi verdict UAT; không tác động sản phẩm hoặc dữ liệu thật.
