// Calculation functions ported from the user-owned Poteri source. No private project data.
// Source calculations SHA-256: 61e33bc891cc55ec2c185c7986c1583882b7f25aad72e227db3ac849771fb679
globalThis.PoteriEngine = (() => {
const HEAT_CONSTANTS = {
  R_SI: 0.115, // Сопротивление теплоотдаче внутренней поверхности, м2·°С/Вт
  R_SE: 0.043, // Сопротивление теплоотдаче наружной поверхности, м2·°С/Вт
  GROUND_FLOOR_ZONES_R: [2.1, 4.3, 8.6, 14.2], // Сопротивления зон пола по грунту
  GRAVITY: 9.81,
  RHO_REF: 353, // Для расчета разности давлений 3463 / T = (g * 353) / T. 353 = 273 * 1.29 (примерно)
  AIR_HEAT_CAPACITY: 1.006, // кДж/(кг·°С)
  INF_CONST: 0.28 // Коэффициент перевода единиц для формулы Q = 0.28 * G * c * dT
};


const CONSTRUCTION_TYPE_MAP = {
  wall: 'НС',
  window: 'ОК',
  door: 'НД',
  slab: 'ПБ',
  floor_on_ground: 'ПГ'
};

const getConstructionTypeName = (type) =>
  CONSTRUCTION_TYPE_MAP[type] || type?.toUpperCase() || '—';

const getFloorData = (floor, building) => {
  if (floor?.linkedTo) {
    const sourceFloor = building?.floors?.find((f) => f.id == floor.linkedTo);
    if (sourceFloor) {
      return {
        ...sourceFloor,
        id: floor.id,
        number: floor.number,
        linkedTo: floor.linkedTo,
        height:
          floor.height !== undefined && floor.height !== null
            ? floor.height
            : sourceFloor.height,
        baseElevation:
          floor.baseElevation ?? sourceFloor.baseElevation ?? 0
      };
    }
  }
  return floor;
};

const computeWallR0FromTemplate = (libraries, template) => {
  if (!template) return 0;
  const { R_SI, R_SE } = HEAT_CONSTANTS;
  const mats = libraries?.materials || [];
  let sum = 0;
  (template.layers || []).forEach((layer) => {
    if (layer.material === 'air') {
      sum += parseFloat(layer.thickness || 0);
    } else {
      const material = mats.find((m) => m.id == layer.material);
      const lambda = parseFloat(material?.lambda);
      if (lambda > 0 && layer.thickness) {
        sum += parseFloat(layer.thickness) / lambda;
      }
    }
  });
  const r = parseFloat(template.r || template.r0 || 0.92); // Коэфф. теплотехнической однородности
  return r * (R_SI + sum + R_SE);
};

const getTemplate = (libraries, type, templateId) => {
  const key =
    type === 'wall'
      ? 'walls'
      : type === 'slab'
        ? 'slabs'
        : type === 'floor_on_ground'
          ? 'groundFloors'
          : type === 'window'
            ? 'windows'
            : 'doors';
  return libraries?.[key]?.find((item) => item.id == templateId);
};

const computeBuildingHeight = (building) => {
  if (!building?.floors) return 0;
  return building.floors.reduce(
    (acc, floor) => {
      const floorData = getFloorData(floor, building);
      return acc + (parseFloat(floorData.height) || 0);
    },
    0
  );
};

// findFloorForRoom removed as it is no longer needed with explicit floor passing

const getRoomVolume = (room, floor) => {
  const height = parseFloat(floor?.height) || 0;
  const area = parseFloat(room?.area) || 0;
  return area * height;
};

const computeOpeningTopHeight = (construction, room, floor) => {
  const base =
    floor && floor.baseElevation != null ? parseFloat(floor.baseElevation) : 0;
  return base + (parseFloat(construction.height) || 0);
};

const getRoomExteriorTemp = (room, building) => {
  const roomTemp = Number(room?.tempExt);
  if (room?.tempExt !== undefined && room?.tempExt !== null && room?.tempExt !== '' && Number.isFinite(roomTemp)) {
    return roomTemp;
  }

  const buildingTemp = Number(building?.tempExt);
  return Number.isFinite(buildingTemp) ? buildingTemp : 0;
};

const getConstructionArea = (construction, room) => {
  if (!construction) return 0;
  if (construction.area !== undefined && construction.area !== null && construction.area !== '') {
    return Number(construction.area) || 0;
  }
  if (construction.type === 'slab' && room?.area) {
    return Number(room.area) || 0;
  }
  if (construction.type === 'floor_on_ground') {
    return (parseFloat(construction.zone1Area) || 0) +
      (parseFloat(construction.zone2Area) || 0) +
      (parseFloat(construction.zone3Area) || 0) +
      (parseFloat(construction.zone4Area) || 0);
  }
  const width = parseFloat(construction.width) || 0;
  const height = parseFloat(construction.height) || 0;
  return width * height;
};

const calculateConstructionHeat = (
  libraries,
  construction,
  room,
  building,
  floor
) => {
  if (!construction || !room || !building) return 0;

  // n - коэффициент положения поверхности
  const n = construction.n_coef ? parseFloat(construction.n_coef) : 1;

  const baseDeltaT = (Number(room.tempInt) || 0) - getRoomExteriorTemp(room, building);

  const orientation = construction.orientation;
  // Добавочные потери на ориентацию
  const orientationBonus = orientation
    ? ['С', 'В', 'СВ', 'СЗ'].includes(orientation)
      ? 0.1
      : 0.05
    : 0;

  let r0 = construction.r0 || 1;
  if (construction.type === 'wall' || construction.type === 'slab') {
    const template = getTemplate(libraries, construction.type, construction.templateId);
    if (template) {
      r0 = computeWallR0FromTemplate(libraries, template);
    }
  } else if (construction.type === 'window' || construction.type === 'door') {
    // Для окон и дверей используем актуальное значение r0 из шаблона
    const template = getTemplate(libraries, construction.type, construction.templateId);
    if (template && template.r0 != null) {
      r0 = parseFloat(template.r0);
    }
  } else if (construction.type === 'floor_on_ground') {
    const template = getTemplate(libraries, construction.type, construction.templateId);
    let r_ins = 0;
    if (template) {
      const mats = libraries?.materials || [];
      (template.layers || []).forEach((layer) => {
        if (layer.material === 'air') return;
        const material = mats.find((m) => m.id == layer.material);
        const lambda = parseFloat(material?.lambda);
        if (lambda > 0 && layer.thickness) {
          r_ins += parseFloat(layer.thickness) / lambda;
        }
      });
    }

    const R_ZONES = HEAT_CONSTANTS.GROUND_FLOOR_ZONES_R;
    const areas = [
      parseFloat(construction.zone1Area) || 0,
      parseFloat(construction.zone2Area) || 0,
      parseFloat(construction.zone3Area) || 0,
      parseFloat(construction.zone4Area) || 0
    ];

    // Для пола по грунту коэффициент n всегда равен 1
    const deltaT = baseDeltaT;

    let qSum = 0;
    areas.forEach((area, idx) => {
      if (area > 0) {
        const r_total = R_ZONES[idx] + r_ins;
        if (r_total > 0) {
          qSum += (area * deltaT) / r_total;
        }
      }
    });

    return qSum;
  }

  const area = getConstructionArea(construction, room);

  if (!area || !r0) return 0;

  // Для остальных конструкций применяем коэффициент n
  const deltaT = n * baseDeltaT;

  // Основная формула трансмиссионных потерь
  return (
    (area * deltaT / r0) *
    (1 + orientationBonus + parseFloat(room.cornerBonus || 0))
  );
};

const getInfiltrationParameters = (
  libraries,
  construction,
  room,
  building,
  floor
) => {
  const exteriorTemp = getRoomExteriorTemp(room, building);
  const deltaT = (Number(room?.tempInt) || 0) - exteriorTemp;
  const baseResult = {
    deltaT,
    deltaP: 0,
    f: 0,
    gn: 0,
    ai: 0,
    area: getConstructionArea(construction, room),
    gInf: 0,
    qInf: 0
  };

  if (!construction || !room || !building) {
    return baseResult;
  }
  // Инфильтрация рассчитывается только для окон и дверей
  if (['wall', 'slab', 'floor_on_ground'].includes(construction.type)) {
    return baseResult;
  }

  const H = computeBuildingHeight(building);
  const h = computeOpeningTopHeight(construction, room, floor);
  const V = building.windSpeed || 0;
  const interiorTemp = Number(room.tempInt) || 0;

  // Расчет разности давлений (Па)
  // 3463 = 9.81 * 353 (константы g * rho_ref)
  const g_rho_ref = HEAT_CONSTANTS.GRAVITY * HEAT_CONSTANTS.RHO_REF;
  const deltaP =
    (H - h) *
    (g_rho_ref / (273 + exteriorTemp) - g_rho_ref / (273 + interiorTemp)) +
    (0.5 * 1.2 * V * V * 1.4);

  const template = getTemplate(libraries, construction.type, construction.templateId);

  // f - показатель степени (2/3 для окон, 1/2 для дверей)
  const f =
    template && template.f != null
      ? parseFloat(template.f)
      : construction.type === 'window'
        ? 0.67
        : 0.5;

  // gn - нормативная воздухопроницаемость при 10 Па (кг/(м2*ч))
  const gn = template && template.gn != null ? parseFloat(template.gn) : 0;

  // ai - коэффициент учета встречного теплового потока (k)
  const ai = template && template.ai != null ? parseFloat(template.ai) : 1;

  const area = baseResult.area;

  // Расчет массы инфильтрующегося воздуха (кг/ч)
  const gInf = gn > 0
    ? area * Math.pow(Math.max(deltaP, 0) / 10, f) * gn
    : 0;

  // Расчет затрат тепла на нагрев инфильтрационного воздуха (Вт)
  const qInf = HEAT_CONSTANTS.INF_CONST * gInf * deltaT * ai;

  return {
    deltaT,
    deltaP,
    f,
    gn,
    ai,
    area,
    gInf,
    qInf
  };
};

const calculateInfiltration = (libraries, construction, room, building, floor) =>
  getInfiltrationParameters(libraries, construction, room, building, floor).qInf;

const calculateRoomHeat = (libraries, room, building, floor) => {
  if (!room) return { qogr: 0, qinf: 0, qVent: 0, total: 0 };

  let qogr = 0;
  let qinf = 0;

  (room.constructions || []).forEach((construction) => {
    qogr += calculateConstructionHeat(libraries, construction, room, building, floor);
    qinf += calculateInfiltration(libraries, construction, room, building, floor);
  });

  // const volume = getRoomVolume(room, floor);
  const qVent = 0;

  const total = Math.max(0, qogr + qinf + qVent - (parseFloat(room.heatGain) || 0));

  return { qogr, qinf, qVent, total };
};

const calculateFloorHeat = (libraries, floor, building) => {
  const floorData = getFloorData(floor, building);
  if (!floorData?.rooms) return 0;
  return floorData.rooms.reduce(
    (sum, room) => sum + calculateRoomHeat(libraries, room, building, floorData).total,
    0
  );
};

const calculateBuildingHeat = (libraries, building) => {
  if (!building?.floors) return 0;
  return building.floors.reduce(
    (sum, floor) => sum + calculateFloorHeat(libraries, floor, building),
    0
  );
};

const cloneWithNewIds = (entity, allocateId) => {
  const clone = JSON.parse(JSON.stringify(entity));
  const assignIds = (item) => {
    if (!item || typeof item !== 'object') return;
    if (Array.isArray(item)) {
      item.forEach(assignIds);
      return;
    }
    if ('id' in item) {
      item.id = allocateId();
    }
    Object.values(item).forEach(assignIds);
  };
  assignIds(clone);
  return clone;
};

/**
 * Рассчитывает площади зон пола по грунту согласно СП 50.13330.2012.
 * 
 * Логика расчета:
 * 1. Зоны отсчитываются от уровня земли.
 * 2. Если есть подвал (H > 0), начало зон приходится на стены.
 * 3. Зоны "переходят" со стен на пол.
 *    - Если глубина H < 2м: Зона I частично на стене, частично на полу.
 *    - Если глубина H > 2м: Зона I полностью на стене. Зона II начинается на стене или полу.
 * 
 * @param {number} width - Ширина помещения (м)
 * @param {number} length - Длина помещения (м)
 * @param {object} activeWalls - Стены, контактирующие с грунтом {top, right, bottom, left}
 * @param {number} basementDepth - Глубина подвала (м), 0 для обычного пола
 * @returns {{floor: {zone1, zone2, zone3, zone4}, walls: {zone1, zone2, zone3, zone4}}}
 */
const calculateGroundFloorZones = (width, length, activeWalls = {}, basementDepth = 0) => {
  const w = parseFloat(width) || 0;
  const l = parseFloat(length) || 0;
  const H = parseFloat(basementDepth) || 0;

  const emptyResult = {
    floor: { zone1: 0, zone2: 0, zone3: 0, zone4: 0 },
    walls: { zone1: 0, zone2: 0, zone3: 0, zone4: 0 }
  };

  if (w <= 0 || l <= 0) return emptyResult;

  const { top, right, bottom, left } = activeWalls;
  const isBasement = H > 0;

  // Используем только выбранные стены для расчета отступов
  const effectiveWalls = { top, right, bottom, left };

  // Вспомогательная функция для расчета внутренней площади с отступом
  const getInnerArea = (offset) => {
    const dx = (effectiveWalls.left ? offset : 0) + (effectiveWalls.right ? offset : 0);
    const dy = (effectiveWalls.top ? offset : 0) + (effectiveWalls.bottom ? offset : 0);
    return Math.max(0, w - dx) * Math.max(0, l - dy);
  };

  // === РАСЧЕТ ЗОН НА ПОЛУ ===
  // Границы зон на полу зависят от глубины подвала
  // Если глубина H, то граница зоны X на полу будет max(0, BoundaryX - H)
  const T1 = Math.max(0, 2 - H);
  const T2 = Math.max(0, 4 - H);
  const T3 = Math.max(0, 6 - H);

  const fullArea = w * l;
  const areaAfterZone1 = getInnerArea(T1);
  const areaAfterZone2 = getInnerArea(T2);
  const areaAfterZone3 = getInnerArea(T3);

  const floorZones = {
    zone1: parseFloat((fullArea - areaAfterZone1).toFixed(2)),
    zone2: parseFloat((areaAfterZone1 - areaAfterZone2).toFixed(2)),
    zone3: parseFloat((areaAfterZone2 - areaAfterZone3).toFixed(2)),
    zone4: parseFloat(areaAfterZone3.toFixed(2))
  };

  // === РАСЧЕТ ЗОН НА СТЕНАХ (только для подвала) ===
  let wallZones = { zone1: 0, zone2: 0, zone3: 0, zone4: 0 };

  if (isBasement) {
    // Периметр активных стен
    const perimeter =
      (effectiveWalls.top ? w : 0) +
      (effectiveWalls.bottom ? w : 0) +
      (effectiveWalls.left ? l : 0) +
      (effectiveWalls.right ? l : 0);

    // Границы зон от уровня земли вниз
    const ZONE_BOUNDARIES = [0, 2, 4, 6, Infinity];

    // Для каждой зоны рассчитываем, какая её часть попадает на стены
    for (let i = 0; i < 4; i++) {
      const zoneStart = ZONE_BOUNDARIES[i];
      const zoneEnd = ZONE_BOUNDARIES[i + 1];

      // Высота этой зоны на стенах (от 0 до H метров от земли)
      const wallHeight = Math.max(0, Math.min(H, zoneEnd) - Math.max(0, zoneStart));

      if (wallHeight > 0 && perimeter > 0) {
        const wallArea = perimeter * wallHeight;
        const zoneKey = `zone${i + 1}`;
        wallZones[zoneKey] = parseFloat(wallArea.toFixed(2));
      }
    }
  }

  return {
    floor: floorZones,
    walls: wallZones
  };
};

return {computeWallR0FromTemplate, computeBuildingHeight, computeOpeningTopHeight, getInfiltrationParameters, calculateConstructionHeat, calculateRoomHeat, calculateFloorHeat, calculateBuildingHeat, calculateGroundFloorZones};
})();
