const now = new Date();

function isHiddenRoot(unit) {
  const code = String(unit.code || "").trim().toUpperCase();
  const fullName = String(unit.fullName || "").trim().toUpperCase();
  const shortName = String(unit.shortName || "").trim().toUpperCase();
  const symbol = String(unit.symbol || "").trim().toUpperCase();

  return !unit.parentUnitId && (
    code === "" || code === "100" || code === "ROOT" ||
    fullName === "ROOT" || fullName === "ROOT UNIT" ||
    shortName === "ROOT" || shortName === "ROOT UNIT" ||
    symbol === "ROOT" || symbol === "ROOT UNIT"
  );
}

db.units.find({
  isDeleted: { $ne: true },
  isVirtual: { $ne: true },
  symbol: { $type: "string", $ne: "" }
}).forEach((unit) => {
  if (isHiddenRoot(unit)) {
    return;
  }

  const unitId = unit._id;
  const unitIdText = unitId.valueOf();
  const symbol = unit.symbol.trim().toLowerCase();
  const unitName = unit.shortName || unit.fullName || symbol;

  db.users.updateOne(
    {
      unitId: { $in: [unitId, unitIdText] },
      accountKind: "UNIT_MANAGER",
      isDeleted: { $ne: true }
    },
    {
      $set: {
        username: `mu_${symbol}`,
        fullName: `Quản trị đơn vị ${unitName}`,
        roles: [`MANAGER_UNIT:${unitIdText}`],
        updatedAtUtc: now
      }
    }
  );

  const existingLevelManager = db.users.findOne({
    unitId: { $in: [unitId, unitIdText] },
    accountKind: "LEVEL_MANAGER",
    isDeleted: { $ne: true }
  });

  if (existingLevelManager) {
    db.users.updateOne(
      { _id: existingLevelManager._id },
      {
        $set: {
          username: `ml_${symbol}`,
          fullName: `Quản trị cấp ${unit.level} - ${unitName}`,
          roles: ["MANAGER_LEVEL"],
          updatedAtUtc: now
        }
      }
    );
    return;
  }

  const legacyLevelManager = db.users.findOne({
    username: `ml_${unit.level}`,
    accountKind: "LEVEL_MANAGER",
    isDeleted: { $ne: true }
  });

  if (!legacyLevelManager) {
    return;
  }

  delete legacyLevelManager._id;
  legacyLevelManager.username = `ml_${symbol}`;
  legacyLevelManager.fullName = `Quản trị cấp ${unit.level} - ${unitName}`;
  legacyLevelManager.unitId = unitId;
  legacyLevelManager.roles = ["MANAGER_LEVEL"];
  legacyLevelManager.createdAtUtc = now;
  legacyLevelManager.updatedAtUtc = now;
  legacyLevelManager.isDeleted = false;
  legacyLevelManager.deletedAtUtc = null;
  legacyLevelManager.deletedByUserId = null;
  db.users.insertOne(legacyLevelManager);
});

db.users.updateMany(
  {
    accountKind: "LEVEL_MANAGER",
    unitId: { $in: [null, ""] },
    username: /^ml_\d+$/i,
    isDeleted: { $ne: true }
  },
  {
    $set: {
      isDeleted: true,
      deletedAtUtc: now,
      updatedAtUtc: now
    }
  }
);

db.refresh_tokens.updateMany(
  {},
  {
    $set: {
      revokedAt: now
    }
  }
);
