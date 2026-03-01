using MongoDB.Bson;

namespace tdtd_be.Common
{
    public class BsonHelper
    {
        private static DateTime GetDateTimeOrDefault(BsonDocument d, string name, DateTime fallbackUtc)
        {
            if (!d.TryGetValue(name, out var v) || v.IsBsonNull) return fallbackUtc;

            // Mongo có thể lưu DateTime hoặc string tùy lịch sử
            if (v.IsValidDateTime) return v.ToUniversalTime();

            if (v.BsonType == MongoDB.Bson.BsonType.String &&
                DateTime.TryParse(v.AsString, out var parsed))
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

            return fallbackUtc;
        }

        private static string GetStringOrEmpty(BsonDocument d, string name)
        {
            if (!d.TryGetValue(name, out var v) || v.IsBsonNull) return "";
            return v.ToString();
        }

        private static bool GetBoolOrFalse(BsonDocument d, string name)
        {
            if (!d.TryGetValue(name, out var v) || v.IsBsonNull) return false;
            if (v.BsonType == MongoDB.Bson.BsonType.Boolean) return v.AsBoolean;
            if (v.BsonType == MongoDB.Bson.BsonType.String && bool.TryParse(v.AsString, out var b)) return b;
            return false;
        }
    }
}
