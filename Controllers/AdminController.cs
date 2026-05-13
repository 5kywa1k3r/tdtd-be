using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Controllers
{
    [ApiController]
    [Route("api/system")]
    public class SystemBootstrapController : ControllerBase
    {
        private const string BootstrapKeyHeaderName = "X-System-Bootstrap-Key";

        private readonly MongoDbContext _ctx;
        private readonly IPasswordHasher<AppUser> _hasher;
        private readonly IConfiguration _cfg;

        public SystemBootstrapController(
            MongoDbContext ctx,
            IPasswordHasher<AppUser> hasher,
            IConfiguration cfg)
        {
            _ctx = ctx;
            _hasher = hasher;
            _cfg = cfg;
        }

        private IActionResult? ValidateBootstrapKey(string? provided)
        {
            var expected = _cfg["SystemBootstrap:Key"];
            if (string.IsNullOrWhiteSpace(expected))
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "System bootstrap key is not configured."
                });

            if (string.IsNullOrWhiteSpace(provided) || !FixedTimeEquals(provided, expected))
                return Unauthorized(new { message = "Invalid system bootstrap key." });

            return null;
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        [HttpPost("bootstrap")]
        public async Task<IActionResult> Bootstrap(
            [FromHeader(Name = BootstrapKeyHeaderName)] string? bootstrapKey,
            CancellationToken ct)
        {
            var keyError = ValidateBootstrapKey(bootstrapKey);
            if (keyError is not null) return keyError;

            var now = DateTime.UtcNow;
            const string ROOT_CODE = "100";
            const string TYPE0_CODE = "TYPE0";
            const string ADMIN_USER = "admin";
            const string ADMIN_FULL_NAME = "Quản trị viên hệ thống";
            const string DEFAULT_ADMIN_PASSWORD = "Admin@123";

            bool rootCreated = false;
            bool rootUpdated = false;
            bool type0Upserted = false;
            bool adminCreated = false;
            bool adminUpdated = false;
            bool adminPasswordSet = false;

            // =========================
            // 1) Find or Create ROOT
            // =========================
            var root = await _ctx.Units.Find(x => x.ParentUnitId == null && x.Code == ROOT_CODE)
                .FirstOrDefaultAsync(ct);

            if (root is null)
                root = await _ctx.Units.Find(x => x.ParentUnitId == null && x.Code == "")
                    .FirstOrDefaultAsync(ct);

            if (root is null)
                root = await _ctx.Units.Find(x => x.ParentUnitId == null && x.FullName == "ROOT")
                    .FirstOrDefaultAsync(ct);

            if (root is null)
                root = await _ctx.Units.Find(x => x.ParentUnitId == null)
                    .FirstOrDefaultAsync(ct);

            if (root is null)
            {
                root = new Unit
                {
                    FullName = "ROOT",
                    ShortName = "ROOT",
                    Symbol = "ROOT",
                    Code = ROOT_CODE,
                    Level = 0,
                    ParentUnitId = null,
                    Version = 1,
                    UnitTypeCodes = ["TYPE0"],
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                await _ctx.Units.InsertOneAsync(root, cancellationToken: ct);
                rootCreated = true;
            }
            else
            {
                // Nếu root đang không phải code 100 thì chuyển về 100 (nếu không bị trùng)
                if (!string.Equals(root.Code, ROOT_CODE, StringComparison.Ordinal))
                {
                    var other100 = await _ctx.Units.Find(x => x.Code == ROOT_CODE && x.Id != root.Id)
                        .FirstOrDefaultAsync(ct);

                    if (other100 != null)
                    {
                        return Conflict(new
                        {
                            message = "Cannot repair bootstrap: another unit already uses root code 100.",
                            conflictingUnitId = other100.Id
                        });
                    }
                }

                var update = Builders<Unit>.Update
                    .Set(x => x.FullName, "ROOT")
                    .Set(x => x.Code, ROOT_CODE)
                    .Set(x => x.Level, 0)
                    .Set(x => x.ParentUnitId, null)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UnitTypeCodes, ["TYPE0"]);

                var ur = await _ctx.Units.UpdateOneAsync(x => x.Id == root.Id, update, cancellationToken: ct);
                rootUpdated = ur.ModifiedCount > 0;

                // reload root to get latest
                root = await _ctx.Units.Find(x => x.Id == root.Id).FirstAsync(ct);
            }

            // =========================
            // 2) Upsert UnitType TYPE0
            // =========================
            var type0Update = Builders<UnitType>.Update
                .SetOnInsert(x => x.Code, TYPE0_CODE)
                .Set(x => x.Name, "Quản trị hệ thống (toàn quyền)")
                .Set(x => x.IsDeleted, false)
                .Set(x => x.UpdatedAtUtc, now)
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.Version, 1);

            var type0Res = await _ctx.UnitTypes.UpdateOneAsync(
                x => x.Code == TYPE0_CODE,
                type0Update,
                new UpdateOptions { IsUpsert = true },
                ct);

            type0Upserted = type0Res.UpsertedId != null || type0Res.ModifiedCount > 0;

            // =========================
            // 3) Upsert ADMIN user
            // =========================
            var admin = await _ctx.Users.Find(x => x.Username == ADMIN_USER).FirstOrDefaultAsync(ct);

            if (admin is null)
            {
                admin = new AppUser
                {
                    Username = ADMIN_USER,
                    FullName = ADMIN_FULL_NAME,
                    UnitId = root.Id,
                    PositionCode = null,
                    Roles = new List<string> { "ADMIN", "SYSTEM_ADMIN" },
                    AccountKind = "SYSTEM_ADMIN",
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                admin.PasswordHash = _hasher.HashPassword(admin, DEFAULT_ADMIN_PASSWORD);
                adminPasswordSet = true;

                await _ctx.Users.InsertOneAsync(admin, cancellationToken: ct);
                adminCreated = true;
            }
            else
            {
                // đảm bảo có ADMIN role
                var roles = admin.Roles ?? new List<string>();
                if (!roles.Contains("ADMIN")) roles.Add("ADMIN");
                if (!roles.Contains("SYSTEM_ADMIN")) roles.Add("SYSTEM_ADMIN");

                var adminUpdate = Builders<AppUser>.Update
                    .Set(x => x.FullName, ADMIN_FULL_NAME)
                    .Set(x => x.UnitId, root.Id)
                    .Set(x => x.PositionCode, null)
                    .Set(x => x.Roles, roles)
                    .Set(x => x.AccountKind, "SYSTEM_ADMIN")
                    .Set(x => x.IsDeleted, false)
                    .Set(x => x.UpdatedAtUtc, now);

                // chỉ set password nếu đang trống (tránh reset password ngoài ý muốn)
                if (string.IsNullOrWhiteSpace(admin.PasswordHash))
                {
                    adminUpdate = adminUpdate.Set(x => x.PasswordHash, _hasher.HashPassword(admin, DEFAULT_ADMIN_PASSWORD));
                    adminPasswordSet = true;
                }

                var ar = await _ctx.Users.UpdateOneAsync(x => x.Id == admin.Id, adminUpdate, cancellationToken: ct);
                adminUpdated = ar.ModifiedCount > 0;
            }

            return Ok(new
            {
                message = "Bootstrap repaired (idempotent).",
                rootUnitId = root.Id,
                rootCode = ROOT_CODE,
                rootCreated,
                rootUpdated,
                type0Upserted,
                adminUsername = ADMIN_USER,
                adminCreated,
                adminUpdated,
                adminPasswordSet,
                defaultPassword = adminPasswordSet ? DEFAULT_ADMIN_PASSWORD : null
            });
        }
    }
}
