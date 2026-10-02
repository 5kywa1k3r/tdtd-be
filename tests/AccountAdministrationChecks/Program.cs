using MongoDB.Bson;
using tdtd_be.Common.Auth;
using tdtd_be.Models;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
Position P(string code, int rank = 0, params string[] types) => new() { Code = code, Name = code, Rank = rank, UnitTypeCodes = types.ToList() };
UnitType T(string code) => new() { Code = code, Name = code };

foreach (var wrong in new[] { "TINH", "PHONG", "XA", "PHUONG_XA", "TO", "" })
    Check(!AccountAdministrationRules.ChildUnitTypeAllowed("PHONG", wrong), "Phòng must reject " + wrong);
Check(AccountAdministrationRules.ChildUnitTypeAllowed("PHONG", "DOI"), "Phòng can create Đội");
foreach (var parent in new[] { "DOI", "XA", "PHUONG", "PHUONG_XA" })
{
    Check(AccountAdministrationRules.ChildUnitTypeAllowed(parent, "TO"), parent + " can create Tổ");
    Check(!AccountAdministrationRules.ChildUnitTypeAllowed(parent, "DOI"), parent + " cannot create same/upper unit");
}
Check(!AccountAdministrationRules.ChildUnitTypeAllowed("TO", "TO"), "Tổ is a leaf");
Check(!AccountAdministrationRules.ChildUnitTypeAllowed(null, "TO"), "Missing type fails closed");
Check(!AccountAdministrationRules.PositionAllowed(P("GIAM_DOC_CAT", 1, "PHONG"), T("PHONG")), "Misconfigured director rank/mapping cannot elevate a phòng user");
Check(AccountAdministrationRules.PositionAllowed(P("TRUONG_PHONG", 80, "PHONG"), T("PHONG")), "Phòng leader allowed");
Check(AccountAdministrationRules.PositionAllowed(P("PHO_TRUONG_PHONG", 70, "PHONG"), T("PHONG")), "Phòng deputy allowed");
Check(!AccountAdministrationRules.PositionAllowed(P("TRUONG_PHONG", 1, "DOI"), T("DOI")), "Đội rejects phòng title despite mapping");
Check(AccountAdministrationRules.PositionAllowed(P("DOI_TRUONG", 60, "DOI"), T("DOI")), "Đội leader allowed");
Check(!AccountAdministrationRules.PositionAllowed(P("DOI_TRUONG", 100, "DOI"), T("DOI")), "Known leader with elevated catalog rank is rejected");
Check(AccountAdministrationRules.PositionAllowed(P("PHO_DOI_TRUONG", 50, "DOI"), T("DOI")), "Đội deputy allowed");
Check(!AccountAdministrationRules.PositionAllowed(P("DOI_TRUONG", 1, "PHUONG_XA"), T("PHUONG_XA")), "Commune rejects team title");
var commune = new Unit { Symbol = "CAX_TEST" };
var ward = new Unit { Symbol = "CAP_TEST" };
Check(AccountAdministrationRules.PositionAllowed(P("TRUONG_CONG_AN_XA", 60, "PHUONG_XA"), T("PHUONG_XA"), commune), "Old seed rank still allows commune leader");
Check(AccountAdministrationRules.PositionAllowed(P("PHO_TRUONG_CONG_AN_XA", 50, "PHUONG_XA"), T("PHUONG_XA"), commune), "Old seed rank still allows commune deputy");
Check(!AccountAdministrationRules.PositionAllowed(P("TRUONG_CONG_AN_PHUONG", 60, "PHUONG_XA"), T("PHUONG_XA"), commune), "Commune rejects ward title");
Check(AccountAdministrationRules.PositionAllowed(P("TRUONG_CONG_AN_PHUONG", 60, "PHUONG_XA"), T("PHUONG_XA"), ward), "Ward leader allowed");
Check(!AccountAdministrationRules.PositionAllowed(P("TRUONG_CONG_AN_XA", 60, "PHUONG_XA"), T("PHUONG_XA"), ward), "Ward rejects commune title");
var rulesType = T("TO");
rulesType.PositionRules = [new() { PositionCode = "TO_TRUONG", IsEnabled = true }];
Check(AccountAdministrationRules.PositionAllowed(P("TO_TRUONG", 40, "PHUONG_XA"), rulesType), "Rules allow existing leader on newly seeded Tổ");
Check(!AccountAdministrationRules.PositionAllowed(P("PHO_TO_TRUONG", 10, "TO"), rulesType), "No Tổ phó");
rulesType.PositionRules[0].IsEnabled = false;
Check(!AccountAdministrationRules.PositionAllowed(P("TO_TRUONG", 20, "TO"), rulesType), "Disabled rule fails closed");
var custom = P("CUSTOM", 100, "DOI");
Check(!AccountAdministrationRules.PositionAllowed(custom, T("DOI")), "Custom high rank is rejected");
custom.Rank = 10;
Check(AccountAdministrationRules.PositionAllowed(custom, T("DOI")), "Configured lower custom rank is allowed");
custom.IsDeleted = true;
Check(!AccountAdministrationRules.PositionAllowed(custom, T("DOI")), "Deleted position rejected");

foreach (var relative in new[] { "seeds/foundation/tdtd-20260924/foundation.json", "seeds/prod/mu_pv01-prod-seed.json" })
{
    var package = BsonDocument.Parse(File.ReadAllText(Path.Combine(args[0], relative)));
    var collections = package["collections"].AsBsonDocument;
    var types = collections.GetValue("unit_types", collections.GetValue("unitTypes", new BsonArray())).AsBsonArray;
    var positions = collections["positions"].AsBsonArray;
    AccountCatalogSeedChecks.Validate(types, positions);
    var team = types.Select(x => x.AsBsonDocument).Single(x => x["code"] == "TO");
    var leader = positions.Select(x => x.AsBsonDocument).Single(x => x["code"] == "TO_TRUONG");
    Check(AccountAdministrationRules.PositionAllowed(
        new Position { Code = leader["code"].AsString, Rank = leader["rank"].ToInt32(), UnitTypeCodes = leader["unitTypeCodes"].AsBsonArray.Select(x => x.AsString).ToList() },
        new UnitType { Code = "TO", PositionRules = [new() { PositionCode = "TO_TRUONG" }] }), relative + " has a usable leader");
    team["positionRules"].AsBsonArray.Add(new BsonDocument { { "positionCode", "PHO_TO_TRUONG" }, { "isEnabled", true } });
    try { AccountCatalogSeedChecks.Validate(types, positions); throw new Exception("Invalid deputy seed was accepted"); }
    catch (InvalidOperationException) { checks++; }
}
Console.WriteLine($"PASS: {checks} account administration and seed contract checks. No database writes.");
