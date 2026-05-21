// state.js — глобальний стан додатку

let map;
let buildingMarkers = [];
let roadPolylines = [];
let buildings = [];
let sidewalks = [];
let buildingRequirements = [];
let roadRequirements = [];
let currentFilter = 'all';
let currentMobilityType = 'all';
let currentLayer = 'buildings';
let graphNodes = [];
let nodeMarkers = [];
let bridgeRoads = [];
let showBridges = false;
let reroutedBridgePolylines = [];
let showReroutedBridges = false;
