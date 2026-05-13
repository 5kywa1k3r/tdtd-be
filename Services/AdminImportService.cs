using System.Text;
using ClosedXML.Excel;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.AdminImport;
using tdtd_be.DTOs.Units;
using tdtd_be.DTOs.Users.Admin;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IAdminImportService
{
    Task<ImportTemplateFile> BuildUnitTemplateAsync(string? format, CancellationToken ct);
    Task<ImportTemplateFile> BuildUserTemplateAsync(string? format, CancellationToken ct);
    Task<ImportResult> ImportUnitsAsync(IFormFile file, bool dryRun, CancellationToken ct);
    Task<ImportResult> ImportUsersAsync(IFormFile file, bool dryRun, CancellationToken ct);
}

public sealed class AdminImportService : IAdminImportService
{
    private static readonly string[] UnitHeaders =
    {
        "externalKey",
        "parentExternalKey",
        "parentUnitCode",
        "quantity",
        "expectedCode",
        "fullName",
        "shortName",
        "symbol",
        "primaryUnitTypeCode",
        "isVirtual",
        "note"
    };

    private static readonly string[] UserHeaders =
    {
        "username",
        "password",
        "fullName",
        "unitCode",
        "positionCode",
        "roles",
        "note"
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IUnitService _units;
    private readonly IUserAdminService _users;

    public AdminImportService(
        MongoDbContext ctx,
        MeAccessor me,
        IUnitService units,
        IUserAdminService users)
    {
        _ctx = ctx;
        _me = me;
        _units = units;
        _users = users;
    }

    public async Task<ImportTemplateFile> BuildUnitTemplateAsync(string? format, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var fmt = NormalizeFormat(format);
        if (fmt == "csv")
            return BuildCsvTemplate("unit-import-template.csv", UnitHeaders, new[]
            {
                new[] { "U001", "", "ROOT", "1", "", "Công an tỉnh", "CAT", "CAT", "CAT", "false", "" },
                new[] { "U002", "U001", "", "1", "", "Phòng Tham mưu", "PV01", "PV01", "PHONG", "false", "" }
            });

        var unitTypes = await _ctx.UnitTypes.Find(x => !x.IsDeleted).SortBy(x => x.Code).ToListAsync(ct);
        var units = await _ctx.Units.Find(x => !x.IsDeleted).SortBy(x => x.Code).Limit(500).ToListAsync(ct);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Units");
        WriteHeader(ws, UnitHeaders);
        WriteRow(ws, 2, new[] { "U001", "", "ROOT", "1", "", "Công an tỉnh", "CAT", "CAT", "CAT", "false", "" });
        WriteRow(ws, 3, new[] { "U002", "U001", "", "1", "", "Phòng Tham mưu", "PV01", "PV01", "PHONG", "false", "" });
        ws.Columns().AdjustToContents();

        var typeWs = wb.AddWorksheet("UnitTypes");
        WriteHeader(typeWs, new[] { "code", "name" });
        var row = 2;
        foreach (var type in unitTypes)
            WriteRow(typeWs, row++, new[] { type.Code, type.Name });
        typeWs.Columns().AdjustToContents();

        var unitWs = wb.AddWorksheet("ExistingUnits");
        WriteHeader(unitWs, new[] { "code", "name", "primaryUnitTypeCode", "isVirtual" });
        row = 2;
        foreach (var unit in units)
            WriteRow(unitWs, row++, new[] { unit.Code ?? "", unit.ShortName ?? unit.FullName, unit.PrimaryUnitTypeCode ?? "", unit.IsVirtual ? "true" : "false" });
        unitWs.Columns().AdjustToContents();

        return BuildXlsxTemplate(wb, "unit-import-template.xlsx");
    }

    public async Task<ImportTemplateFile> BuildUserTemplateAsync(string? format, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireUserManagementRole(me);

        var fmt = NormalizeFormat(format);
        if (fmt == "csv")
            return BuildCsvTemplate("user-import-template.csv", UserHeaders, new[]
            {
                new[] { "nguyenvana", "123456@Aa", "Nguyen Van A", "001", "TRUONG_PHONG", "", "" }
            });

        var positions = await _ctx.Positions.Find(x => !x.IsDeleted).SortBy(x => x.Order).ThenBy(x => x.Code).ToListAsync(ct);
        var units = await _ctx.Units.Find(x => !x.IsDeleted && !x.IsVirtual).SortBy(x => x.Code).Limit(500).ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Users");
        WriteHeader(ws, UserHeaders);
        WriteRow(ws, 2, new[] { "nguyenvana", "123456@Aa", "Nguyen Van A", "001", "TRUONG_PHONG", "", "" });
        ws.Columns().AdjustToContents();

        var positionWs = wb.AddWorksheet("Positions");
        WriteHeader(positionWs, new[] { "code", "name", "unitTypeCodes" });
        var row = 2;
        foreach (var position in positions)
            WriteRow(positionWs, row++, new[] { position.Code, position.Name, string.Join(",", position.UnitTypeCodes ?? new()) });
        positionWs.Columns().AdjustToContents();

        var unitWs = wb.AddWorksheet("Units");
        WriteHeader(unitWs, new[] { "code", "name", "primaryUnitTypeCode", "isVirtual" });
        row = 2;
        foreach (var unit in units)
            WriteRow(unitWs, row++, new[] { unit.Code ?? "", unit.ShortName ?? unit.FullName, unit.PrimaryUnitTypeCode ?? "", unit.IsVirtual ? "true" : "false" });
        unitWs.Columns().AdjustToContents();

        return BuildXlsxTemplate(wb, "user-import-template.xlsx");
    }

    public async Task<ImportResult> ImportUnitsAsync(IFormFile file, bool dryRun, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var rows = await ReadRowsAsync(file, UnitHeaders, ct);
        var errors = new List<ImportRowError>();
        var parsed = rows.Select(x => new UnitImportRow(
            x.RowNumber,
            Value(x, "externalKey"),
            Value(x, "parentExternalKey"),
            PositionAdminService.NormalizeOptionalCode(Value(x, "parentUnitCode")) ?? "",
            Value(x, "quantity"),
            Value(x, "expectedCode"),
            Value(x, "fullName"),
            Value(x, "shortName"),
            Value(x, "symbol"),
            PositionAdminService.NormalizeOptionalCode(Value(x, "primaryUnitTypeCode")) ?? "",
            Value(x, "isVirtual"),
            Value(x, "note")
        )).ToList();

        RequireRows(parsed.Count, errors);
        AddRequiredErrors(parsed, errors, x => x.ExternalKey, "externalKey");
        AddRequiredErrors(parsed, errors, x => x.QuantityRaw, "quantity");
        AddRequiredErrors(parsed, errors, x => x.FullName, "fullName");
        AddRequiredErrors(parsed, errors, x => x.PrimaryUnitTypeCode, "primaryUnitTypeCode");
        AddMaxLengthErrors(parsed, errors, x => x.ParentUnitCode, "parentUnitCode", 50);
        AddMaxLengthErrors(parsed, errors, x => x.ExpectedCode, "expectedCode", 50);
        AddMaxLengthErrors(parsed, errors, x => x.FullName, "fullName", 500);
        AddMaxLengthErrors(parsed, errors, x => x.ShortName, "shortName", 300);
        AddMaxLengthErrors(parsed, errors, x => x.Symbol, "symbol", 30);
        AddMaxLengthErrors(parsed, errors, x => x.PrimaryUnitTypeCode, "primaryUnitTypeCode", 50);
        AddDuplicateErrors(parsed, errors, x => x.ExternalKey, "externalKey");

        foreach (var row in parsed)
        {
            if (!TryParseImportBool(row.IsVirtualRaw, out _))
                errors.Add(new(row.RowNumber, "isVirtual", "INVALID_BOOL", "isVirtual must be true/false, 1/0, yes/no, or blank."));

            if (!TryParseQuantity(row.QuantityRaw, out var quantity))
                errors.Add(new(row.RowNumber, "quantity", "INVALID_QUANTITY", "quantity must be a positive integer."));
            else if (quantity != 1)
                errors.Add(new(row.RowNumber, "quantity", "QUANTITY_NOT_SUPPORTED", "Each import row creates exactly one unit. Split multiple units into separate rows so BE can generate codes sequentially."));

            if (!string.IsNullOrWhiteSpace(row.ParentExternalKey) && !string.IsNullOrWhiteSpace(row.ParentUnitCode))
                errors.Add(new(row.RowNumber, "parentUnitCode", "MULTIPLE_PARENT_SOURCES", "Use either parentExternalKey or parentUnitCode, not both."));
        }

        var keySet = parsed.Select(x => x.ExternalKey).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in parsed.Where(x => !string.IsNullOrWhiteSpace(x.ParentExternalKey)))
        {
            if (!keySet.Contains(row.ParentExternalKey))
                errors.Add(new(row.RowNumber, "parentExternalKey", "PARENT_NOT_FOUND", "Parent external key is not found in file."));
        }

        AddCycleErrors(parsed, errors);

        var importPlan = await BuildUnitImportPlanAsync(parsed, errors, ct);

        var typeCodes = parsed.Select(x => x.PrimaryUnitTypeCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingTypes = await _ctx.UnitTypes.Find(x => typeCodes.Contains(x.Code) && !x.IsDeleted).Project(x => x.Code).ToListAsync(ct);
        foreach (var missing in typeCodes.Except(existingTypes, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var row in parsed.Where(x => string.Equals(x.PrimaryUnitTypeCode, missing, StringComparison.OrdinalIgnoreCase)))
                errors.Add(new(row.RowNumber, "primaryUnitTypeCode", "UNIT_TYPE_NOT_FOUND", "Unit type does not exist or is deleted."));
        }

        AddDuplicateErrors(parsed.Where(x => !string.IsNullOrWhiteSpace(x.Symbol)).ToList(), errors, x => x.Symbol, "symbol");

        var symbols = parsed.Select(x => x.Symbol).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (symbols.Count > 0)
        {
            var existingSymbols = await _ctx.Units.Find(x => x.Symbol != null && symbols.Contains(x.Symbol) && !x.IsDeleted).Project(x => x.Symbol).ToListAsync(ct);
            foreach (var symbol in existingSymbols.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var row in parsed.Where(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
                    errors.Add(new(row.RowNumber, "symbol", "SYMBOL_EXISTS", "Unit symbol already exists."));
            }
        }

        if (errors.Count > 0 || dryRun)
            return BuildResult(parsed.Count, errors, Array.Empty<string>(), importPlan.Rows);

        var createdIds = new List<string>();
        var createdByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingByCode = importPlan.ExistingByCode;
        foreach (var row in TopologicalSort(parsed))
        {
            var parentId = string.IsNullOrWhiteSpace(row.ParentExternalKey)
                ? ResolveExistingParentId(row.ParentUnitCode, existingByCode)
                : createdByKey[row.ParentExternalKey];

            var created = await _units.CreateAsync(new CreateUnitRequest
            {
                FullName = row.FullName,
                ShortName = EmptyToNull(row.ShortName),
                Symbol = EmptyToNull(row.Symbol),
                ParentUnitId = parentId,
                PrimaryUnitTypeCode = row.PrimaryUnitTypeCode,
                IsVirtual = TryParseImportBool(row.IsVirtualRaw, out var isVirtual) && isVirtual,
                UnitTypeCodes = new List<string>()
            }, ct);

            createdIds.Add(created.Id);
            createdByKey[row.ExternalKey] = created.Id;
            importPlan.SetCreatedId(row.RowNumber, created.Id);
        }

        return BuildResult(parsed.Count, errors, createdIds, importPlan.Rows);
    }

    public async Task<ImportResult> ImportUsersAsync(IFormFile file, bool dryRun, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireUserManagementRole(me);

        var rows = await ReadRowsAsync(file, UserHeaders, ct);
        var errors = new List<ImportRowError>();
        var parsed = rows.Select(x => new UserImportRow(
            x.RowNumber,
            Value(x, "username").Trim().ToLowerInvariant(),
            Value(x, "password"),
            Value(x, "fullName"),
            Value(x, "unitCode"),
            PositionAdminService.NormalizeOptionalCode(Value(x, "positionCode")) ?? "",
            Value(x, "roles"),
            Value(x, "note")
        )).ToList();

        RequireRows(parsed.Count, errors);
        AddRequiredErrors(parsed, errors, x => x.Username, "username");
        AddRequiredErrors(parsed, errors, x => x.Password, "password");
        AddRequiredErrors(parsed, errors, x => x.FullName, "fullName");
        AddRequiredErrors(parsed, errors, x => x.UnitCode, "unitCode");
        AddRequiredErrors(parsed, errors, x => x.PositionCode, "positionCode");
        AddMaxLengthErrors(parsed, errors, x => x.Username, "username", 64);
        AddMaxLengthErrors(parsed, errors, x => x.Password, "password", 128);
        AddMaxLengthErrors(parsed, errors, x => x.FullName, "fullName", 128);
        AddMaxLengthErrors(parsed, errors, x => x.PositionCode, "positionCode", 80);
        AddDuplicateErrors(parsed, errors, x => x.Username, "username");

        foreach (var row in parsed.Where(x => x.Username.Length is > 0 and < 3))
            errors.Add(new(row.RowNumber, "username", "MIN_LENGTH", "Username must be at least 3 characters."));

        foreach (var row in parsed.Where(x => x.Password.Length is > 0 and < 6))
            errors.Add(new(row.RowNumber, "password", "MIN_LENGTH", "Password must be at least 6 characters."));

        var usernames = parsed.Select(x => x.Username).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (usernames.Count > 0)
        {
            var existingUsers = await _ctx.Users.Find(x => usernames.Contains(x.Username) && !x.IsDeleted).Project(x => x.Username).ToListAsync(ct);
            foreach (var username in existingUsers)
            {
                foreach (var row in parsed.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)))
                    errors.Add(new(row.RowNumber, "username", "USERNAME_EXISTS", "Username already exists."));
            }
        }

        var unitCodes = parsed.Select(x => x.UnitCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        var units = await _ctx.Units.Find(x => x.Code != null && unitCodes.Contains(x.Code) && !x.IsDeleted).ToListAsync(ct);
        var unitByCode = units.Where(x => !string.IsNullOrWhiteSpace(x.Code)).ToDictionary(x => x.Code!, StringComparer.Ordinal);

        foreach (var row in parsed)
        {
            if (!unitByCode.TryGetValue(row.UnitCode, out var unit))
            {
                errors.Add(new(row.RowNumber, "unitCode", "UNIT_NOT_FOUND", "Unit code does not exist or is deleted."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(unit.PrimaryUnitTypeCode))
            {
                errors.Add(new(row.RowNumber, "unitCode", "UNIT_TYPE_MISSING", "Target unit has no primary unit type."));
                continue;
            }

            if (unit.IsVirtual)
            {
                errors.Add(new(row.RowNumber, "unitCode", "UNIT_IS_VIRTUAL", "Cannot import users into a virtual unit."));
                continue;
            }

            var positionValid = await _ctx.Positions
                .Find(x => x.Code == row.PositionCode && !x.IsDeleted && x.UnitTypeCodes.Contains(unit.PrimaryUnitTypeCode))
                .AnyAsync(ct);

            if (!positionValid)
                errors.Add(new(row.RowNumber, "positionCode", "POSITION_INVALID", "Position is invalid for target unit type."));
        }

        foreach (var row in parsed)
        {
            foreach (var role in ParseRoles(row.Roles))
            {
                if (string.Equals(role, Roles.ADMIN, StringComparison.OrdinalIgnoreCase))
                    errors.Add(new(row.RowNumber, "roles", "ROLE_NOT_ALLOWED", "ADMIN role cannot be imported."));
            }
        }

        if (errors.Count > 0 || dryRun)
            return BuildResult(parsed.Count, errors, Array.Empty<string>());

        var createdIds = new List<string>();
        foreach (var row in parsed)
        {
            var created = await _users.CreateAsync(new CreateUserRequest
            {
                Username = row.Username,
                Password = row.Password,
                FullName = row.FullName,
                UnitId = unitByCode[row.UnitCode].Id,
                PositionCode = row.PositionCode,
                Roles = ParseRoles(row.Roles)
            }, ct);

            createdIds.Add(created.Id);
        }

        return BuildResult(parsed.Count, errors, createdIds);
    }

    private static ImportResult BuildResult(
        int totalRows,
        IReadOnlyList<ImportRowError> errors,
        IReadOnlyList<string> createdIds,
        IReadOnlyList<ImportPreviewRow>? rows = null)
    {
        var errorRows = errors.Select(x => x.RowNumber).Distinct().Count();
        return new ImportResult(totalRows, totalRows - errorRows, errorRows, errors, createdIds)
        {
            Rows = rows ?? Array.Empty<ImportPreviewRow>()
        };
    }

    private async Task<UnitImportPlan> BuildUnitImportPlanAsync(
        IReadOnlyList<UnitImportRow> rows,
        List<ImportRowError> errors,
        CancellationToken ct)
    {
        var existingUnits = await _ctx.Units
            .Find(x => !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        var existingByCode = existingUnits
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.ParentUnitCode) && !IsRootParentCode(x.ParentUnitCode)))
        {
            if (!existingByCode.ContainsKey(row.ParentUnitCode))
                errors.Add(new(row.RowNumber, "parentUnitCode", "PARENT_UNIT_NOT_FOUND", "Parent unit code does not exist or is deleted."));
        }

        var hiddenRoot = existingUnits
            .Where(x => string.IsNullOrWhiteSpace(x.ParentUnitId))
            .FirstOrDefault(IsHiddenRootUnit);

        var planRows = new List<ImportPreviewRow>();
        var generatedByExternalKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextRoot = GetNextRootSequence(existingUnits);
        var nextChildByParentCode = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in TopologicalSort(rows))
        {
            string? parentCode = null;
            var canPlan = true;

            if (!string.IsNullOrWhiteSpace(row.ParentExternalKey))
            {
                canPlan = generatedByExternalKey.TryGetValue(row.ParentExternalKey, out parentCode);
            }
            else if (!string.IsNullOrWhiteSpace(row.ParentUnitCode))
            {
                if (IsRootParentCode(row.ParentUnitCode))
                {
                    parentCode = hiddenRoot?.Code;
                }
                else if (existingByCode.TryGetValue(row.ParentUnitCode, out var parent))
                {
                    parentCode = parent.Code;
                }
                else
                {
                    canPlan = false;
                }
            }
            else
            {
                parentCode = hiddenRoot?.Code;
            }

            string? generatedCode = null;
            if (canPlan)
            {
                generatedCode = string.IsNullOrWhiteSpace(parentCode)
                    ? (nextRoot++).ToString().PadLeft(3, '0')
                    : NextChildCode(parentCode!, existingUnits, nextChildByParentCode);

                if (!string.IsNullOrWhiteSpace(row.ExternalKey))
                    generatedByExternalKey[row.ExternalKey] = generatedCode;

                if (!string.IsNullOrWhiteSpace(row.ExpectedCode) &&
                    !string.Equals(row.ExpectedCode, generatedCode, StringComparison.Ordinal))
                {
                    errors.Add(new(
                        row.RowNumber,
                        "expectedCode",
                        "CODE_MISMATCH",
                        $"expectedCode does not match BE generated code. Expected {row.ExpectedCode}, generated {generatedCode}."));
                }
            }

            planRows.Add(new ImportPreviewRow(
                row.RowNumber,
                row.ExternalKey,
                row.FullName,
                parentCode,
                generatedCode));
        }

        return new UnitImportPlan(existingByCode, planRows);
    }

    private static string? ResolveExistingParentId(string? parentUnitCode, IReadOnlyDictionary<string, Unit> existingByCode)
    {
        var code = (parentUnitCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code) || IsRootParentCode(code))
            return null;

        return existingByCode.TryGetValue(code, out var parent) ? parent.Id : null;
    }

    private static int GetNextRootSequence(IReadOnlyList<Unit> existingUnits)
    {
        var last = existingUnits
            .Where(x => string.IsNullOrWhiteSpace(x.ParentUnitId) && !string.IsNullOrWhiteSpace(x.Code) && int.TryParse(x.Code, out _))
            .Select(x => int.Parse(x.Code!))
            .DefaultIfEmpty(0)
            .Max();

        return last + 1;
    }

    private static string NextChildCode(
        string parentCode,
        IReadOnlyList<Unit> existingUnits,
        Dictionary<string, int> nextChildByParentCode)
    {
        if (!nextChildByParentCode.TryGetValue(parentCode, out var next))
        {
            var childLevel = parentCode.Length / 3 + 1;
            var last = existingUnits
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Code) &&
                    x.Code!.StartsWith(parentCode, StringComparison.Ordinal) &&
                    x.Level == childLevel &&
                    x.Code.Length >= 3)
                .Select(x => int.TryParse(x.Code![^3..], out var value) ? value : 0)
                .DefaultIfEmpty(0)
                .Max();

            next = last + 1;
        }

        nextChildByParentCode[parentCode] = next + 1;
        return parentCode + next.ToString().PadLeft(3, '0');
    }

    private static bool IsRootParentCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "" or "ROOT" or "ROOT UNIT";
    }

    private static bool IsHiddenRootUnit(Unit unit)
    {
        var code = (unit.Code ?? string.Empty).Trim().ToUpperInvariant();
        var fullName = (unit.FullName ?? string.Empty).Trim().ToUpperInvariant();
        var shortName = (unit.ShortName ?? string.Empty).Trim().ToUpperInvariant();
        var symbol = (unit.Symbol ?? string.Empty).Trim().ToUpperInvariant();

        if (code is "" or "ROOT") return true;
        return string.IsNullOrWhiteSpace(unit.ParentUnitId) &&
               (fullName is "ROOT" or "ROOT UNIT" ||
                shortName is "ROOT" or "ROOT UNIT" ||
                symbol is "ROOT" or "ROOT UNIT");
    }

    private static void RequireRows(int count, List<ImportRowError> errors)
    {
        if (count == 0)
            errors.Add(new(1, "file", "EMPTY_FILE", "Import file has no data rows."));
    }

    private static void AddRequiredErrors<T>(IEnumerable<T> rows, List<ImportRowError> errors, Func<T, string?> getValue, string field)
        where T : IImportRow
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(getValue(row)))
                errors.Add(new(row.RowNumber, field, "REQUIRED", "This field is required."));
        }
    }

    private static void AddDuplicateErrors<T>(IReadOnlyList<T> rows, List<ImportRowError> errors, Func<T, string?> getValue, string field)
        where T : IImportRow
    {
        var groups = rows
            .Select(x => new { Row = x, Value = (getValue(x) ?? "").Trim() })
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var item in group)
                errors.Add(new(item.Row.RowNumber, field, "DUPLICATE_IN_FILE", "Duplicate value in import file."));
        }
    }

    private static void AddMaxLengthErrors<T>(IEnumerable<T> rows, List<ImportRowError> errors, Func<T, string?> getValue, string field, int maxLength)
        where T : IImportRow
    {
        foreach (var row in rows)
        {
            var value = getValue(row) ?? "";
            if (value.Length > maxLength)
                errors.Add(new(row.RowNumber, field, "MAX_LENGTH", $"Maximum length is {maxLength} characters."));
        }
    }

    private static void AddCycleErrors(IReadOnlyList<UnitImportRow> rows, List<ImportRowError> errors)
    {
        var byKey = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey))
            .GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!Visit(row.ExternalKey))
                errors.Add(new(row.RowNumber, "parentExternalKey", "CYCLE", "Parent references create a cycle."));
        }

        bool Visit(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !byKey.TryGetValue(key, out var row))
                return true;

            if (state.TryGetValue(key, out var s))
                return s == 2;

            state[key] = 1;
            if (!string.IsNullOrWhiteSpace(row.ParentExternalKey))
            {
                if (state.TryGetValue(row.ParentExternalKey, out var parentState) && parentState == 1)
                    return false;
                if (!Visit(row.ParentExternalKey))
                    return false;
            }

            state[key] = 2;
            return true;
        }
    }

    private static IReadOnlyList<UnitImportRow> TopologicalSort(IReadOnlyList<UnitImportRow> rows)
    {
        var byKey = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey))
            .GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UnitImportRow>();

        foreach (var row in rows)
            Visit(row);

        return result;

        void Visit(UnitImportRow row)
        {
            var key = string.IsNullOrWhiteSpace(row.ExternalKey) ? $"__row:{row.RowNumber}" : row.ExternalKey;
            if (!visited.Add(key))
                return;

            if (!string.IsNullOrWhiteSpace(row.ParentExternalKey) && byKey.TryGetValue(row.ParentExternalKey, out var parent))
                Visit(parent);

            result.Add(row);
        }
    }

    private static List<string> ParseRoles(string? raw)
        => (raw ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void RequireUserManagementRole(tdtd_be.DTOs.Auth.MeResponse me)
    {
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me) || RoleGuard.IsManagerLevel(me) || RoleGuard.TryGetManagerUnit(me, out _))
            return;

        throw AppExceptionFactory.Forbidden(AppErrorCode.ADMIN_IMPORT_ROLE_REQUIRED, new { me.Id, me.Roles });
    }

    private static string? EmptyToNull(string? value)
    {
        var s = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool TryParseImportBool(string? raw, out bool value)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            value = false;
            return true;
        }

        if (bool.TryParse(s, out value))
            return true;

        if (s == "1")
        {
            value = true;
            return true;
        }

        if (s == "0")
        {
            value = false;
            return true;
        }

        if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "y", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(s, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "n", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryParseQuantity(string? raw, out int quantity)
    {
        var s = (raw ?? string.Empty).Trim();
        return int.TryParse(s, out quantity) && quantity > 0;
    }

    private static string Value(ImportRow row, string key)
        => row.Values.TryGetValue(key, out var value) ? value.Trim() : "";

    private static string NormalizeFormat(string? format)
    {
        var fmt = (format ?? "xlsx").Trim().ToLowerInvariant();
        if (fmt is "xlsx" or "csv")
            return fmt;
        throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_TEMPLATE_FORMAT_UNSUPPORTED, new { format });
    }

    private static ImportTemplateFile BuildCsvTemplate(string fileName, string[] headers, IEnumerable<string[]> exampleRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in exampleRows)
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

        return new ImportTemplateFile(bytes, "text/csv; charset=utf-8", fileName);
    }

    private static ImportTemplateFile BuildXlsxTemplate(XLWorkbook wb, string fileName)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new ImportTemplateFile(
            ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static void WriteHeader(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
        }
    }

    private static void WriteRow(IXLWorksheet ws, int row, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            ws.Cell(row, i + 1).Value = values[i];
    }

    private async Task<List<ImportRow>> ReadRowsAsync(IFormFile file, IReadOnlyList<string> expectedHeaders, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_FILE_REQUIRED, new { fileName = file?.FileName, length = file?.Length });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var stream = file.OpenReadStream();

        return ext switch
        {
            ".csv" => await ReadCsvRowsAsync(stream, expectedHeaders, ct),
            ".xlsx" => ReadXlsxRows(stream, expectedHeaders),
            _ => throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_FILE_TYPE_UNSUPPORTED, new { file.FileName, extension = ext })
        };
    }

    private static async Task<List<ImportRow>> ReadCsvRowsAsync(Stream stream, IReadOnlyList<string> expectedHeaders, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_CSV_EMPTY);

        var headers = ParseCsvLine(lines[0]).Select(x => x.Trim()).ToList();
        EnsureHeaders(headers, expectedHeaders);

        var rows = new List<ImportRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var values = ParseCsvLine(lines[i]);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < expectedHeaders.Count; c++)
                map[expectedHeaders[c]] = c < values.Count ? values[c] : "";

            if (map.Values.All(string.IsNullOrWhiteSpace))
                continue;

            rows.Add(new ImportRow(i + 1, map));
        }

        return rows;
    }

    private static List<ImportRow> ReadXlsxRows(Stream stream, IReadOnlyList<string> expectedHeaders)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var firstRow = ws.FirstRowUsed()
            ?? throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_XLSX_EMPTY);
        var headers = Enumerable.Range(1, expectedHeaders.Count)
            .Select(i => firstRow.Cell(i).GetString().Trim())
            .ToList();
        EnsureHeaders(headers, expectedHeaders);

        var rows = new List<ImportRow>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstRow.RowNumber();
        for (var r = firstRow.RowNumber() + 1; r <= lastRow; r++)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < expectedHeaders.Count; c++)
                map[expectedHeaders[c]] = ws.Cell(r, c + 1).GetString().Trim();

            if (map.Values.All(string.IsNullOrWhiteSpace))
                continue;

            rows.Add(new ImportRow(r, map));
        }

        return rows;
    }

    private static void EnsureHeaders(IReadOnlyList<string> actualHeaders, IReadOnlyList<string> expectedHeaders)
    {
        for (var i = 0; i < expectedHeaders.Count; i++)
        {
            if (i >= actualHeaders.Count || !string.Equals(actualHeaders[i], expectedHeaders[i], StringComparison.OrdinalIgnoreCase))
                throw AppExceptionFactory.BadRequest(AppErrorCode.ADMIN_IMPORT_HEADER_INVALID, new
                {
                    column = i + 1,
                    expected = expectedHeaders[i],
                    actual = i < actualHeaders.Count ? actualHeaders[i] : null
                });
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        result.Add(sb.ToString());
        return result;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private interface IImportRow
    {
        int RowNumber { get; }
    }

    private sealed record ImportRow(int RowNumber, Dictionary<string, string> Values);

    private sealed class UnitImportPlan
    {
        public UnitImportPlan(Dictionary<string, Unit> existingByCode, List<ImportPreviewRow> rows)
        {
            ExistingByCode = existingByCode;
            Rows = rows;
        }

        public Dictionary<string, Unit> ExistingByCode { get; }
        public List<ImportPreviewRow> Rows { get; }

        public void SetCreatedId(int rowNumber, string createdId)
        {
            var index = Rows.FindIndex(x => x.RowNumber == rowNumber);
            if (index >= 0)
                Rows[index] = Rows[index] with { CreatedId = createdId };
        }
    }

    private sealed record UnitImportRow(
        int RowNumber,
        string ExternalKey,
        string ParentExternalKey,
        string ParentUnitCode,
        string QuantityRaw,
        string ExpectedCode,
        string FullName,
        string ShortName,
        string Symbol,
        string PrimaryUnitTypeCode,
        string IsVirtualRaw,
        string Note
    ) : IImportRow;

    private sealed record UserImportRow(
        int RowNumber,
        string Username,
        string Password,
        string FullName,
        string UnitCode,
        string PositionCode,
        string Roles,
        string Note
    ) : IImportRow;
}
