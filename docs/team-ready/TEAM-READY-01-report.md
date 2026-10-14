# TEAM-READY-01 — Kết quả local setup và chuẩn bị Neon

Ngày: 2026-10-14, Asia/Bangkok. Feature branch `codex/fix-01-s07`, HEAD vẫn
`32b0416d1e1052241ba1e78c8a36975b324b705a`. Không commit/push/PR/merge/deploy.

## Kết luận

Source candidate đã thực sự chạy từ thư mục source sạch, không bin/obj/node_modules,
mailbox, private config, backup hay tài khoản PRODUCT-01 được copy. Đây là **clean
source export của candidate chưa commit**, không phải checkout Git của một commit
đã giao. Remote develop chưa có toàn bộ thay đổi này: chỉ sau Delivery 02 được duyệt
và merge mới có thể làm theo guide bằng clone develop. Không tuyên bố Neon/staging
hoặc lịch sử migration đã tương thích.

Hướng dẫn đầy đủ: [LOCAL-SETUP.md](LOCAL-SETUP.md). Review tương lai:
[NEON-COMPATIBILITY.md](NEON-COMPATIBILITY.md). Setup không phụ thuộc runtime Codex.

## Thay đổi thực thi trong batch

- Frontend API mặc định same-origin `/api`; `VITE_API_BASE_URL` tùy chỉnh URL public,
  từ chối credentials/query/fragment/protocol không hợp lệ. Vite proxy đọc
  `VITE_API_PROXY_TARGET`; default 5009 giữ tương thích demo hiện có.
- CORS đọc `Cors:AllowedOrigins`, giữ defaults cũ khi không cấu hình. Email pickup
  directory có thể cấu hình; mặc định `.local-mail` giữ tương thích.
- Redis KeyPrefix tùy chọn, áp dụng cho limiter, resend activation và public cache;
  prefix rỗng giữ behavior cũ, fixture/team app có namespace riêng, không flush Redis.
- Tool `tools/EventTicketing.LocalSetup`: init/verify/fixture rõ ràng, không gọi từ
  API startup. Literal 127.0.0.1:5432, argument/connection target khớp,
  `event_ticketing_test_team_<32 lowercase hex>`; từ chối mọi database tồn tại trước
  CREATE, kể cả rỗng. Không thể nhận tên PRODUCT-01/protected/remote.
- Tạo target mới bằng template0, xác minh rỗng; schema từ production EF model, lọc
  toàn bộ HasData data operations. Chỉ thêm role definitions có sẵn; không thêm user
  seed lịch sử/password hash cũ. Không sửa/khởi tạo __EFMigrationsHistory.
- Verify tables/columns/nullability, indexes, keys/checks và model marker; fingerprint
  catalog columns/constraints/indexes/sequence definitions. Đây không phải migration
  reconciliation hoặc phương án nâng cấp database đã có.
- Fixture Organizer/Admin dùng EF models/PasswordHasher, mật khẩu nhập ẩn hoặc ENV,
  chỉ @example.test và database mới có marker khớp. Collision dừng, không overwrite.
- Thêm local-env.ps1, Compose local riêng, global.json và public frontend env example.
- Browser harness đổi fixed 5009/5173 thành ephemeral ports và proxy theo API fixture.
  Tests dùng Redis prefix riêng theo database; các assertions limiter/cache được giữ
  và đọc đúng scoped keys. CI required API/unit minimum tăng 311 → 327.
- Delivery 02 manifest cập nhật 83 paths: 34 production, 21 tests, 13 CI, 8 setup,
  7 documentation. Chỉ allowlist metadata, không stage/commit.

## Môi trường sạch đã thực sự chạy

- PostgreSQL: `event_ticketing_test_team_ae9d86423a4a4818a657e4ac5b2741d0`,
  endpoint 127.0.0.1:5432, local Docker đã xác minh.
- Redis: 127.0.0.1:6379, DB 15, prefix
  `team_ae9d86423a4a4818a657e4ac5b2741d0:`.
- API `http://127.0.0.1:5011`, frontend `http://127.0.0.1:5175` để không chiếm demo.
- API readiness 200, frontend 200, Redis PONG. Source dependencies restore/npm ci
  từ declared project/lockfile, không copy runtime dependencies từ PRODUCT-01.
- Init và verify chạy thật, không có historical users sau init; schema bao gồm
  sales_transitions. Lệnh local-env trong guide cũng đã chạy với secrets trong bộ nhớ
  và verify schema thành công, không start duplicate process.
- Schema inventory SHA-256 sau init và cuối vẫn
  `E3CCFDBBB5C9DC86B3F5A42D2FA6B27C0F1E51067D9F1238824D19E74FFBAC7E`.

## Kết quả thực tế

| Gate | Kết quả cuối |
| --- | --- |
| Full backend suite | 330 passed / 0 failed / 0 skipped: 327 API/unit + 2 browser + 1 local latency |
| Full frontend suite | 95 passed / 0 failed / 0 skipped |
| Environment safety | 16 passed / 0 failed |
| CI/workflow/manifest guards | 47 passed / 0 failed / 0 skipped; YAML validator pass |
| Backend Release + Debug build từ source sạch | 0 errors / 0 warnings |
| Frontend production build | Pass |
| ESLint / dotnet format / git diff --check | Pass |
| npm ci audit | 0 vulnerabilities ở frontend và tests/ci |

Backend full run có workload 200 requests / 100 seats: 100 success, 100 conflicts,
0 other errors, invariants 100/100. Browser tests thật bao gồm atomic batch conflict/
recovery, feed pagination, release, Available/Held/Sold, 2.000 seats, pinch/tap qua
Chrome mobile emulation. Không thay thế kiểm thử cảm ứng trên thiết bị vật lý.
Local latency 30 samples sau warmup: feed p95 7,13 ms, hold 17,16 ms, release 8,30 ms;
render 2.000 seats khoảng 771,48 ms trong browser fixture này, không phải production SLA.

Clean-setup journey thực sự thực hiện:

1. Buyer Register qua browser, email pickup vào thư mục mới ngoài repo, Activate qua
   link và Login thành công. Buyer role hiện đúng và `/events` báo từ chối.
2. Organizer fixture hợp lệ qua CLI EF, login nhận đúng role; create event owner và
   future showtime qua API; multipart preview không persist rồi import 8 ghế.
3. Publish + OnSale thành công, public feed có eventId 1/showtime 1 và browser Home
   điều hướng đúng detail/seat map. Có 2 audit rows trong sales_transitions.
4. Browser Buyer chọn A1/A2 và atomic hold, hiện 10:00; reload phục hồi 2 ghế với
   countdown đã giảm, không reset. SELECT read-only xác nhận 2 holds/1 deadline,
   khoảng 561 giây còn lại sau các thao tác kiểm tra.
5. Release A1 giữ deadline A2 chính xác; release ghế cuối bỏ session, 8 Available,
   0 HELD ở kiểm tra cuối. Không extend deadline bằng SQL.
6. Foreign Organizer API nhận 403; Admin API được truy cập. Resume-verification
   riêng hoàn tất 5 checks, 0 failed; không cộng nó thành full browser suite giả.

Các lỗi trong quá trình verification đã được xử lý minh bạch:

- Tool mới ban đầu cần pin cả EF Core và Relational 10.0.12 để khớp API; final build
  không conflict warnings. Constructor email pickup giữ tương thích gọi cũ.
- ESLint yêu cầu import process trong Vite config; đã sửa, không nới lint rules.
- Browser mobile assertion cũ so hit-test parent `[data-seat-id]` với SVG g; rect
  mới cũng có attribute nên luôn false. Đổi sang interactive `g[data-seat-id]`, vẫn
  assert exact target/geometry và giữ touch/selection checks; rerun cuối 330/330 pass.
- Private clean-API verifier đọc `items.id` thay vì actual `items.eventId`, mặc dù
  event đã public đúng. Sửa verifier, không sửa production feed contract. Runner
  dừng, không reset tài khoản; passwords ban đầu đã mất khỏi bộ nhớ process sau exit.
  Resume thêm 2 dedicated authorization fixtures mới trong đúng target, không
  overwrite 3 fixtures đã có. Tổng mới: 1 Buyer đăng ký và 5 Organizer/Admin fixtures.
  Mật khẩu ngẫu nhiên không được lưu hoặc commit; không dùng lại seed/demo accounts.

## Bảo toàn và hạn chế

- PRODUCT-01 services tiếp tục PID API 22080/frontend 20208, ready 200; không restart,
  tạo lại DB hoặc reprovision tài khoản demo. Source HMR dùng proxy default tương thích.
- Original backup branch/checkpoint vẫn e9c2a2d; 101 file đối chiếu, 0 mismatch,
  status unchanged. Previous delivery worktree clean, HEAD 1160253c, không thay đổi.
- Mọi suite DB targets đều mới; giữ nguyên các target/evidence sau test, không drop.
  Không ghi schema/data protected DBs hoặc kết nối Neon, chạy historical migration chain/DB-03B,
  xóa volumes/backups hoặc chỉnh source worktree gốc.
- S-15/S-16/pricing/checkout/orders/pending payments không triển khai. S-07.3,
  S-08.3, S-12.4 vẫn Blocked. Không audit lại hoặc nâng trạng thái 61 criteria giả.
- Fresh database không có EF history. Legacy performance_id NOT NULL, unresolved
  337/385, missing coordinates, NULL owners và 6-row history mismatch chưa giải quyết.
- Chưa có GitHub-hosted CI run, real staging/SMTP/rollback, approved shared schema
  rollout hay Neon runtime test. Không thể gọi production/team shared DB ready.
- Extra clean API/frontend giữ chạy trên 5011/5175; database/evidence giữ để kiểm tra.
  PRODUCT-01 vẫn trên 5009/5173. Không restart hoặc cleanup tự động.
- Targeted added-content scan: 0 high-risk credential patterns; không in giá trị
  nhạy cảm. Đây không phải bằng chứng mọi secret trong toàn bộ lịch sử đều sạch.
  Historical credential exposure đã ghi nhận trước đó vẫn cần responsible-owner
  rotation/revocation; không rewrite history trong batch này. Không lưu mật khẩu
  fixture/JWT/DB credentials trong candidate.
- Disk cuối: C khoảng 34,34 GiB, D 36,39 GiB, E 46,69 GiB. Các database test mới
  được giữ lại; không tự drop để thu hồi dung lượng.

## Trước Delivery 02

Review 83-path manifest/content modes và secret diff; cập nhật remote develop metadata
trước integration. Separate approval cần cho delivery branch/worktree, stage/commit,
push, PR và merge. Không có Git write action nào được thực hiện trong batch này.
Neon integration cần leader approval, branch/database riêng, backup/restore verification,
schema/history review, factual legacy decisions và rehearsal riêng. Xem checklist;
không coi code approval là database/deployment approval.

Ảnh/browser/API/TRX evidence lưu riêng ngoài Git, không đưa private mail/token/logs
vào candidate. Các lệnh setup chính xác có trong LOCAL-SETUP.md.
