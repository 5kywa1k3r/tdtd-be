using tdtd_be.DTOs;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.Services
{
    public static class AuditHelper
    {
        public static void StampCreate(BaseEntity e, CurrentUser cu, string? note)
        {
            var now = DateTime.UtcNow;
            e.CreatedAtUtc = now;
            e.UpdatedAtUtc = now;
            e.CreatedByUserId = cu.UserId;
            e.UpdatedByUserId = cu.UserId;
            e.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        }

        public static void StampUpdate(BaseEntity e, CurrentUser cu, string? note)
        {
            e.UpdatedAtUtc = DateTime.UtcNow;
            e.UpdatedByUserId = cu.UserId;
            e.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        }

        public static AuditDto ToDto(BaseEntity e) => new(
            e.CreatedByUserId,
            e.UpdatedByUserId,
            e.CreatedAtUtc,
            e.UpdatedAtUtc,
            e.Note
        );
    }
}
