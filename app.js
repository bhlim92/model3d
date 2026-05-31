// Aura3D - Core Application Logic (Blender Integrated)

// Application State
let scene, camera, renderer, controls;
let currentModelGroup;
let gridHelper, axesHelper;
let ambientLight, dirLight, pointLight;

let currentEngineMode = 'local'; // 'local', 'three', or 'blender'
let geminiApiKey = localStorage.getItem('aura3d_gemini_key') || '';
let geminiModelName = localStorage.getItem('aura3d_gemini_model') || 'gemini-1.5-flash';
let blenderPath = localStorage.getItem('aura3d_blender_path') || '';
let socketMode = localStorage.getItem('aura3d_socket_mode') !== 'false'; // default true
let socketPort = localStorage.getItem('aura3d_socket_port') || '5555';
let lightingMode = 'studio'; // 'studio', 'sunset', 'neon', 'flat'
let wireframeMode = false;
let autoRotate = false;
let generatedScriptCode = ''; // Cache for copy/download
let backendUrl = localStorage.getItem('aura3d_backend_url') || '';

// Color and Shape mappings for Local Parser
const COLOR_MAP = {
  '빨간': 0xff3b30, '빨강': 0xff3b30, '빨간색': 0xff3b30, 'red': 0xff3b30,
  '파란': 0x007aff, '파랑': 0x007aff, '파란색': 0x007aff, 'blue': 0x007aff,
  '초록': 0x34c759, '녹색': 0x34c759, 'green': 0x34c759,
  '노란': 0xffcc00, '노랑': 0xffcc00, '노란색': 0xffcc00, 'yellow': 0xffcc00,
  '주황': 0xff9500, '주황색': 0xff9500, 'orange': 0xff9500,
  '보라': 0xaf52de, '보라색': 0xaf52de, 'purple': 0xaf52de,
  '핑크': 0xff2d55, '분홍': 0xff2d55, '분홍색': 0xff2d55, 'pink': 0xff2d55,
  '흰': 0xffffff, '흰색': 0xffffff, '하얀': 0xffffff, '하얀색': 0xffffff, 'white': 0xffffff,
  '검은': 0x1c1c1e, '검정': 0x1c1c1e, '검은색': 0x1c1c1e, 'black': 0x1c1c1e,
  '회색': 0x8e8e93, 'grey': 0x8e8e93, 'gray': 0x8e8e93,
  '갈색': 0xa0522d, 'brown': 0xa0522d,
  '금색': 0xffd700, '골드': 0xffd700, 'gold': 0xffd700,
  '은색': 0xc0c0c0, '실버': 0xc0c0c0, 'silver': 0xc0c0c0
};

const SHAPE_MAP = {
  '큐브': 'box', '상자': 'box', '박스': 'box', '네모': 'box', 'box': 'box', 'cube': 'box',
  '구': 'sphere', '공': 'sphere', 'sphere': 'sphere', 'ball': 'sphere',
  '실린더': 'cylinder', '원기둥': 'cylinder', 'cylinder': 'cylinder',
  '원뿔': 'cone', 'cone': 'cone',
  '토러스': 'torus', '도넛': 'torus', 'torus': 'torus', 'donut': 'torus',
  '캡슐': 'capsule', 'capsule': 'capsule'
};

const SIZE_MAP = {
  '큰': 1.8, '거대한': 2.5, '대형': 1.8, 'large': 1.8, 'huge': 2.5, 'big': 1.8,
  '작은': 0.5, '미세한': 0.25, '소형': 0.5, 'small': 0.5, 'tiny': 0.25
};

// Initial Setup
document.addEventListener('DOMContentLoaded', () => {
  initThree();
  initUI();
  updateStats();
  
  // Set default theme from UI
  document.body.className = 'theme-dark';
  
  // Load gallery
  renderGallery();
});

// Initialize Three.js Scene
function initThree() {
  const container = document.getElementById('canvas-container');
  const canvas = document.getElementById('three-canvas');
  
  // Scene
  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x090a0f);
  scene.fog = new THREE.FogExp2(0x090a0f, 0.015);
  
  // Camera
  camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 100);
  camera.position.set(5, 5, 8);
  
  // Renderer
  renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, alpha: true });
  renderer.setSize(container.clientWidth, container.clientHeight);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.0;
  
  // Model Group Holder
  currentModelGroup = new THREE.Group();
  scene.add(currentModelGroup);
  
  // Orbit Controls
  controls = new THREE.OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.05;
  controls.maxPolarAngle = Math.PI / 2 - 0.01; // Don't go below ground
  controls.minDistance = 0.1; // Allows zooming in much closer
  controls.maxDistance = 40;  // Allows slightly further zoom out
  
  // Helpers
  gridHelper = new THREE.GridHelper(30, 30, 0x00f0ff, 0x222639);
  gridHelper.position.y = -0.01;
  scene.add(gridHelper);
  
  axesHelper = new THREE.AxesHelper(5);
  scene.add(axesHelper);
  
  // Setup Lighting
  setupLights('studio');
  
  // Handle Resize
  window.addEventListener('resize', onWindowResize);
  
  // Animation Loop
  renderer.setAnimationLoop(animate);
}

// Setup Lights
function setupLights(mode) {
  if (ambientLight) scene.remove(ambientLight);
  if (dirLight) scene.remove(dirLight);
  if (pointLight) scene.remove(pointLight);
  
  lightingMode = mode;
  
  if (mode === 'studio') {
    ambientLight = new THREE.AmbientLight(0xffffff, 0.4);
    dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
    dirLight.position.set(6, 10, 4);
    dirLight.castShadow = true;
    dirLight.shadow.mapSize.width = 2048;
    dirLight.shadow.mapSize.height = 2048;
    dirLight.shadow.bias = -0.0005;
    
    pointLight = new THREE.PointLight(0x00f0ff, 0.5, 10);
    pointLight.position.set(-4, 3, -4);
    
  } else if (mode === 'sunset') {
    ambientLight = new THREE.AmbientLight(0xff7b00, 0.2);
    dirLight = new THREE.DirectionalLight(0xffddaa, 1.2);
    dirLight.position.set(8, 6, 2);
    dirLight.castShadow = true;
    dirLight.shadow.mapSize.width = 1024;
    dirLight.shadow.mapSize.height = 1024;
    
    pointLight = new THREE.PointLight(0xff0088, 0.8, 15);
    pointLight.position.set(-6, 2, 4);
    
  } else if (mode === 'neon') {
    ambientLight = new THREE.AmbientLight(0x0c0032, 0.3);
    dirLight = new THREE.DirectionalLight(0x00f0ff, 0.8);
    dirLight.position.set(5, 5, 5);
    dirLight.castShadow = true;
    dirLight.shadow.mapSize.width = 1024;
    dirLight.shadow.mapSize.height = 1024;
    
    pointLight = new THREE.PointLight(0xff00ff, 1.2, 12);
    pointLight.position.set(-3, 3, 2);
    
  } else if (mode === 'flat') {
    ambientLight = new THREE.AmbientLight(0xffffff, 1.0);
    dirLight = new THREE.DirectionalLight(0xffffff, 0.2);
    dirLight.position.set(0, 10, 0);
  }
  
  scene.add(ambientLight);
  if (dirLight) scene.add(dirLight);
  if (pointLight) scene.add(pointLight);
}

// Animation loop
function animate() {
  controls.update();
  if (autoRotate && currentModelGroup) {
    currentModelGroup.rotation.y += 0.005;
  }
  renderer.render(scene, camera);
}

// Window resize
function onWindowResize() {
  const container = document.getElementById('canvas-container');
  camera.aspect = container.clientWidth / container.clientHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(container.clientWidth, container.clientHeight);
}

// Logger System
function log(message, type = 'info') {
  const output = document.getElementById('log-output');
  const entry = document.createElement('div');
  entry.className = `log-entry ${type}-text`;
  
  const now = new Date();
  const timeStr = `${now.getHours().toString().padStart(2, '0')}:${now.getMinutes().toString().padStart(2, '0')}:${now.getSeconds().toString().padStart(2, '0')}`;
  
  entry.textContent = `[${timeStr}] ${message}`;
  output.appendChild(entry);
  output.scrollTop = output.scrollHeight;
  
  const badge = document.getElementById('status-badge-text');
  badge.className = 'status-badge';
  
  if (type === 'error') {
    badge.textContent = '실패';
    badge.classList.add('error');
  } else if (type === 'success') {
    badge.textContent = '완료';
    badge.classList.add('success');
  } else {
    badge.textContent = '진행 중';
  }
}

// Initialize UI Elements
function initUI() {
  // Navigation Tabs switching
  const tabButtons = document.querySelectorAll('.tab-btn');
  const tabContents = document.querySelectorAll('.tab-content');
  
  tabButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      tabButtons.forEach(b => b.classList.remove('active'));
      tabContents.forEach(c => c.classList.remove('active'));
      
      btn.classList.add('active');
      const tabId = btn.getAttribute('data-tab');
      document.getElementById(tabId).classList.add('active');
    });
  });
  
  // Generation Engine Modes (Local vs Three AI vs Blender AI)
  const modeBtnLocal = document.getElementById('mode-btn-local');
  const modeBtnThree = document.getElementById('mode-btn-three');
  const modeBtnBlender = document.getElementById('mode-btn-blender');
  
  modeBtnLocal.addEventListener('click', () => {
    currentEngineMode = 'local';
    setActiveModeButton(modeBtnLocal);
    document.getElementById('stat-engine').textContent = '로컬 엔진';
    hideCodeContainer();
  });
  
  modeBtnThree.addEventListener('click', () => {
    currentEngineMode = 'three';
    setActiveModeButton(modeBtnThree);
    document.getElementById('stat-engine').textContent = 'Three AI';
    hideCodeContainer();
  });
  
  modeBtnBlender.addEventListener('click', () => {
    currentEngineMode = 'blender';
    setActiveModeButton(modeBtnBlender);
    document.getElementById('stat-engine').textContent = 'Blender AI';
    hideCodeContainer();
  });

  function setActiveModeButton(activeBtn) {
    [modeBtnLocal, modeBtnThree, modeBtnBlender].forEach(btn => btn.classList.remove('active'));
    activeBtn.classList.add('active');
  }
  
  document.getElementById('stat-engine').textContent = '로컬 엔진';
  
  // Presets Clicking
  const presets = document.querySelectorAll('.preset-card');
  presets.forEach(preset => {
    preset.addEventListener('click', () => {
      const prompt = preset.getAttribute('data-prompt');
      document.getElementById('prompt-input').value = prompt;
      generateModel();
    });
  });
  
  // Generate Button Click
  const generateBtn = document.getElementById('generate-btn');
  generateBtn.addEventListener('click', generateModel);
  
  // API Key handling in settings
  const apiKeyInput = document.getElementById('api-key-input');
  if (geminiApiKey) {
    apiKeyInput.value = geminiApiKey;
  }
  apiKeyInput.addEventListener('input', (e) => {
    geminiApiKey = e.target.value.trim();
    localStorage.setItem('aura3d_gemini_key', geminiApiKey);
  });
  
  const toggleKeyVisibility = document.getElementById('toggle-key-visibility');
  toggleKeyVisibility.addEventListener('click', () => {
    const icon = toggleKeyVisibility.querySelector('span');
    if (apiKeyInput.type === 'password') {
      apiKeyInput.type = 'text';
      icon.textContent = 'visibility_off';
    } else {
      apiKeyInput.type = 'password';
      icon.textContent = 'visibility';
    }
  });

  // Model selection handling
  const modelSelect = document.getElementById('model-select');
  const customModelWrapper = document.getElementById('custom-model-wrapper');
  const customModelInput = document.getElementById('custom-model-input');
  
  const savedModelSelect = localStorage.getItem('aura3d_gemini_model_select') || 'gemini-1.5-flash';
  const savedCustomModelName = localStorage.getItem('aura3d_gemini_model_custom') || '';
  
  modelSelect.value = savedModelSelect;
  if (savedModelSelect === 'custom') {
    customModelWrapper.classList.remove('hidden');
    customModelInput.value = savedCustomModelName;
    geminiModelName = savedCustomModelName;
  } else {
    customModelWrapper.classList.add('hidden');
    geminiModelName = savedModelSelect;
  }

  modelSelect.addEventListener('change', (e) => {
    const val = e.target.value;
    localStorage.setItem('aura3d_gemini_model_select', val);
    
    if (val === 'custom') {
      customModelWrapper.classList.remove('hidden');
      geminiModelName = customModelInput.value.trim();
      localStorage.setItem('aura3d_gemini_model', geminiModelName);
    } else {
      customModelWrapper.classList.add('hidden');
      geminiModelName = val;
      localStorage.setItem('aura3d_gemini_model', geminiModelName);
    }
    log(`사용 모델 변경됨: ${geminiModelName}`);
  });

  customModelInput.addEventListener('input', (e) => {
    const val = e.target.value.trim();
    localStorage.setItem('aura3d_gemini_model_custom', val);
    if (modelSelect.value === 'custom') {
      geminiModelName = val;
      localStorage.setItem('aura3d_gemini_model', geminiModelName);
    }
  });

  // Backend URL input in settings
  const backendUrlInput = document.getElementById('backend-url-input');
  if (backendUrlInput) {
    backendUrlInput.value = backendUrl;
    backendUrlInput.addEventListener('input', (e) => {
      backendUrl = e.target.value.trim();
      localStorage.setItem('aura3d_backend_url', backendUrl);
    });
  }

  // Blender path input in settings
  const blenderPathInput = document.getElementById('blender-path-input');
  if (blenderPath) {
    blenderPathInput.value = blenderPath;
  }
  blenderPathInput.addEventListener('input', (e) => {
    blenderPath = e.target.value.trim();
    localStorage.setItem('aura3d_blender_path', blenderPath);
  });

  // Blender Socket settings
  const setSocketMode = document.getElementById('setting-socket-mode');
  setSocketMode.checked = socketMode;
  setSocketMode.addEventListener('change', (e) => {
    socketMode = e.target.checked;
    localStorage.setItem('aura3d_socket_mode', socketMode);
    log(`실시간 블렌더 소켓 연동: ${socketMode ? '활성화' : '비활성화'}`);
  });

  const socketPortInput = document.getElementById('socket-port-input');
  if (socketPort) {
    socketPortInput.value = socketPort;
  }
  socketPortInput.addEventListener('input', (e) => {
    socketPort = e.target.value.trim() || '5555';
    localStorage.setItem('aura3d_socket_port', socketPort);
  });

  // Download Blender Socket Server Python script
  const downloadServerScriptBtn = document.getElementById('btn-download-server-script');
  downloadServerScriptBtn.addEventListener('click', () => {
    log("블렌더 연동 서버 파이썬 파일 다운로드 요청 중...");
    fetch(getApiUrl('/blender_socket_server.py'))
      .then(response => {
        if (!response.ok) throw new Error("서버 파일 로드 실패");
        return response.text();
      })
      .then(pythonCode => {
        downloadFile(pythonCode, 'blender_socket_server.py', 'text/plain');
        log("블렌더 연동 서버 파일 다운로드 완료! 블렌더에서 실행해 주세요.", "success");
      })
      .catch(err => {
        log("파일 다운로드 중 에러: " + err.message, "error");
        alert("다운로드 중 에러가 발생했습니다. 직접 파일을 생성하시려면 프로젝트 폴더의 blender_socket_server.py를 사용해 주세요.");
      });
  });
  
  // Setting checkboxes
  document.getElementById('setting-grid').addEventListener('change', (e) => {
    gridHelper.visible = e.target.checked;
  });
  
  document.getElementById('setting-axes').addEventListener('change', (e) => {
    axesHelper.visible = e.target.checked;
  });
  
  document.getElementById('setting-shadows').addEventListener('change', (e) => {
    renderer.shadowMap.enabled = e.target.checked;
    currentModelGroup.traverse(node => {
      if (node.isMesh) {
        node.material.needsUpdate = true;
      }
    });
  });
  
  document.getElementById('setting-autorotate').addEventListener('change', (e) => {
    autoRotate = e.target.checked;
  });
  
  // Background themes selectors
  const themeButtons = document.querySelectorAll('.theme-btn');
  themeButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      themeButtons.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      
      const theme = btn.getAttribute('data-theme');
      document.body.className = `theme-${theme}`;
      
      let colorHex = 0x090a0f;
      if (theme === 'gray') colorHex = 0x1c1d24;
      if (theme === 'blue') colorHex = 0x081120;
      if (theme === 'light') colorHex = 0xeef1f6;
      
      scene.background.setHex(colorHex);
      scene.fog.color.setHex(colorHex);
    });
  });
  
  // Viewer Control Panel
  document.getElementById('btn-reset-camera').addEventListener('click', () => {
    camera.position.set(5, 5, 8);
    controls.target.set(0, 0, 0);
    controls.update();
    log("카메라 각도가 복구되었습니다.");
  });
  
  document.getElementById('btn-toggle-wireframe').addEventListener('click', () => {
    wireframeMode = !wireframeMode;
    currentModelGroup.traverse(node => {
      if (node.isMesh && node.material) {
        node.material.wireframe = wireframeMode;
      }
    });
    log(`와이어프레임 모드: ${wireframeMode ? '활성화' : '비활성화'}`);
  });
  
  document.getElementById('btn-toggle-light').addEventListener('click', () => {
    const lightModes = ['studio', 'sunset', 'neon', 'flat'];
    let nextIndex = (lightModes.indexOf(lightingMode) + 1) % lightModes.length;
    setupLights(lightModes[nextIndex]);
    log(`조명 테마 변경: ${lightModes[nextIndex].toUpperCase()}`);
  });
  
  // Exports Dropdown
  const exportBtn = document.getElementById('export-dropdown-btn');
  const exportMenu = document.getElementById('export-menu');
  
  exportBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    exportBtn.classList.toggle('active');
    exportMenu.classList.toggle('show');
  });
  
  document.addEventListener('click', () => {
    exportBtn.classList.remove('active');
    exportMenu.classList.remove('show');
  });
  
  const exportItems = document.querySelectorAll('.export-item');
  exportItems.forEach(item => {
    item.addEventListener('click', () => {
      const format = item.getAttribute('data-format');
      export3DModel(format);
    });
  });
  
  // Clear Gallery Button
  document.getElementById('clear-gallery-btn').addEventListener('click', () => {
    localStorage.removeItem('aura3d_gallery');
    renderGallery();
    log("갤러리 히스토리가 비워졌습니다.");
  });

  // Code Actions
  document.getElementById('btn-copy-code').addEventListener('click', () => {
    if (!generatedScriptCode) return;
    navigator.clipboard.writeText(generatedScriptCode)
      .then(() => log("코드가 클립보드에 복사되었습니다.", "success"))
      .catch(err => alert("복사 실패: " + err));
  });

  document.getElementById('btn-download-code').addEventListener('click', () => {
    if (!generatedScriptCode) return;
    const isBlender = currentEngineMode === 'blender';
    const ext = isBlender ? 'py' : 'js';
    const filename = isBlender ? 'blender_script.py' : 'three_script.js';
    const mime = isBlender ? 'text/plain' : 'application/javascript';
    downloadFile(generatedScriptCode, filename, mime);
    log(`스크립트 파일 다운로드 완료 (${filename})`);
  });

  document.getElementById('btn-run-blender-local').addEventListener('click', runBlenderLocalProcess);

  // Launch Blender GUI Buttons
  const launchBlenderBtn = document.getElementById('btn-launch-blender-gui');
  const launchBlenderShortcutBtn = document.getElementById('btn-launch-blender-gui-shortcut');
  if (launchBlenderBtn) {
    launchBlenderBtn.addEventListener('click', launchBlenderGUI);
  }
  if (launchBlenderShortcutBtn) {
    launchBlenderShortcutBtn.addEventListener('click', launchBlenderGUI);
  }
}

// Show/Hide code containers
function showCodeContainer(title, code, showRunBtn = false) {
  const container = document.getElementById('code-output-container');
  const titleText = document.getElementById('code-title-text');
  const codeBlock = document.getElementById('code-block-display');
  const runBtn = document.getElementById('btn-run-blender-local');
  const guidePanel = document.getElementById('blender-guide-panel');

  generatedScriptCode = code;
  titleText.textContent = title;
  codeBlock.textContent = code;
  container.classList.remove('hidden');

  if (showRunBtn) {
    runBtn.classList.remove('hidden');
    // If blenderPath is empty, show manual guide automatically
    if (!blenderPath) {
      guidePanel.classList.remove('hidden');
    } else {
      guidePanel.classList.add('hidden');
    }
  } else {
    runBtn.classList.add('hidden');
    guidePanel.classList.add('hidden');
  }
}

function hideCodeContainer() {
  document.getElementById('code-output-container').classList.add('hidden');
  document.getElementById('blender-guide-panel').classList.add('hidden');
}

// Calculate total vertices and faces of current group
function updateStats() {
  let vertices = 0;
  let faces = 0;
  
  currentModelGroup.traverse(node => {
    if (node.isMesh && node.geometry) {
      const geom = node.geometry;
      if (geom.index) {
        faces += geom.index.count / 3;
      } else if (geom.attributes.position) {
        faces += geom.attributes.position.count / 3;
      }
      
      if (geom.attributes.position) {
        vertices += geom.attributes.position.count;
      }
    }
  });
  
  document.getElementById('stat-vertices').textContent = Math.round(vertices).toLocaleString();
  document.getElementById('stat-faces').textContent = Math.round(faces).toLocaleString();
}

// Clear scene group
function clearModelGroup() {
  while (currentModelGroup.children.length > 0) {
    const object = currentModelGroup.children[0];
    currentModelGroup.remove(object);
    
    object.traverse(child => {
      if (child.isMesh) {
        if (child.geometry) child.geometry.dispose();
        if (child.material) {
          if (Array.isArray(child.material)) {
            child.material.forEach(mat => mat.dispose());
          } else {
            child.material.dispose();
          }
        }
      }
    });
  }
  currentModelGroup.rotation.set(0, 0, 0);
}

// Focus camera on object
function focusCameraOnObject() {
  const box = new THREE.Box3().setFromObject(currentModelGroup);
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  
  const maxDim = Math.max(size.x, size.y, size.z);
  if (maxDim === 0) return; // empty

  const fov = camera.fov * (Math.PI / 180);
  let cameraZ = Math.abs(maxDim / 2 / Math.tan(fov / 2));
  cameraZ *= 1.8;
  
  camera.position.set(center.x + cameraZ * 0.6, center.y + cameraZ * 0.6, center.z + cameraZ * 0.8);
  controls.target.copy(center);
  controls.update();
}

// Generate Model Main Controller
async function generateModel() {
  const promptInput = document.getElementById('prompt-input');
  const prompt = promptInput.value.trim();
  
  if (!prompt) {
    alert("3D 모델에 대한 프롬프트를 입력하세요.");
    return;
  }
  
  const statusPanel = document.getElementById('status-panel');
  const progressBar = document.getElementById('progress-bar');
  
  statusPanel.classList.remove('hidden');
  progressBar.style.width = '10%';
  
  clearModelGroup();
  updateStats();
  hideCodeContainer();
  
  log(`"${prompt}" 생성 프로세스 시작 (엔진: ${currentEngineMode.toUpperCase()})...`);
  
  try {
    if (currentEngineMode === 'local') {
      await generateModelLocally(prompt, progressBar);
    } else if (currentEngineMode === 'three') {
      await generateModelThreeAI(prompt, progressBar);
    } else if (currentEngineMode === 'blender') {
      await generateModelBlenderAI(prompt, progressBar);
    }
  } catch (err) {
    log(`오류 발생: ${err.message}`, 'error');
  }
}

// -------------------------------------------------------------
// 1. LOCAL PROCEDURAL GENERATION ENGINE
// -------------------------------------------------------------
async function generateModelLocally(prompt, progressBar) {
  log("로컬 자연어 파서 가동 중...");
  progressBar.style.width = '30%';
  await sleep(300);
  
  const cleanPrompt = prompt.toLowerCase().replace(/\s+/g, ' ');
  let generated = false;
  progressBar.style.width = '60%';
  
  if (cleanPrompt.includes('눈사람') || cleanPrompt.includes('snowman')) {
    createSnowmanPreset();
    generated = true;
  } else if (cleanPrompt.includes('테이블') || cleanPrompt.includes('식탁') || cleanPrompt.includes('table')) {
    createTablePreset();
    generated = true;
  } else if (cleanPrompt.includes('의자') || cleanPrompt.includes('chair')) {
    createChairPreset();
    generated = true;
  } else if (cleanPrompt.includes('나무') || cleanPrompt.includes('tree') || cleanPrompt.includes('소나무')) {
    createTreePreset();
    generated = true;
  } else if (cleanPrompt.includes('검') || cleanPrompt.includes('칼') || cleanPrompt.includes('sword') || cleanPrompt.includes('무기')) {
    createSwordPreset();
    generated = true;
  } else if (cleanPrompt.includes('집') || cleanPrompt.includes('house') || cleanPrompt.includes('주택')) {
    createHousePreset();
    generated = true;
  }
  
  if (!generated) {
    const items = prompt.split(/,|그리고|and|\+/g);
    let shapesCreatedCount = 0;
    let lastPositionX = 0;
    let stackObjects = [];
    
    for (let i = 0; i < items.length; i++) {
      const itemStr = items[i].trim();
      if (!itemStr) continue;
      
      let shapeType = 'box';
      for (const [key, val] of Object.entries(SHAPE_MAP)) {
        if (itemStr.includes(key)) {
          shapeType = val;
          break;
        }
      }
      
      let hexColor = 0x9e9e9e;
      for (const [key, val] of Object.entries(COLOR_MAP)) {
        if (itemStr.includes(key)) {
          hexColor = val;
          break;
        }
      }
      
      let sizeScale = 1.0;
      for (const [key, val] of Object.entries(SIZE_MAP)) {
        if (itemStr.includes(key)) {
          sizeScale = val;
          break;
        }
      }
      
      const material = new THREE.MeshStandardMaterial({
        color: hexColor,
        roughness: 0.4,
        metalness: 0.1,
        wireframe: wireframeMode
      });
      
      let geometry;
      let height = 1.0 * sizeScale;
      let width = 1.0 * sizeScale;
      let depth = 1.0 * sizeScale;
      
      if (shapeType === 'box') {
        geometry = new THREE.BoxGeometry(width, height, depth);
      } else if (shapeType === 'sphere') {
        geometry = new THREE.SphereGeometry(width / 2, 32, 32);
      } else if (shapeType === 'cylinder') {
        geometry = new THREE.CylinderGeometry(width / 2, width / 2, height, 32);
      } else if (shapeType === 'cone') {
        geometry = new THREE.ConeGeometry(width / 2, height, 32);
      } else if (shapeType === 'torus') {
        geometry = new THREE.TorusGeometry(width * 0.4, width * 0.15, 16, 100);
      } else if (shapeType === 'capsule') {
        geometry = new THREE.CapsuleGeometry(width / 2.5, height * 0.6, 8, 16);
      }
      
      const mesh = new THREE.Mesh(geometry, material);
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      
      const isStack = itemStr.includes('위에') || itemStr.includes('위에다') || itemStr.includes('above') || itemStr.includes('on top');
      
      if (isStack && stackObjects.length > 0) {
        const baseMesh = stackObjects[stackObjects.length - 1];
        baseMesh.geometry.computeBoundingBox();
        const baseBounds = baseMesh.geometry.boundingBox;
        const baseHeight = (baseBounds.max.y - baseBounds.min.y) * baseMesh.scale.y;
        
        mesh.position.x = baseMesh.position.x;
        mesh.position.z = baseMesh.position.z;
        mesh.position.y = baseMesh.position.y + (baseHeight / 2) + (height / 2);
      } else {
        mesh.position.x = lastPositionX;
        mesh.position.y = height / 2;
        mesh.position.z = 0;
        lastPositionX += width + 0.8;
      }
      
      currentModelGroup.add(mesh);
      stackObjects.push(mesh);
      shapesCreatedCount++;
    }
    
    if (shapesCreatedCount === 0) {
      log("정확한 키워드를 감지하지 못해 예시 씬을 로드합니다.", "error");
      createDefaultShowcase();
    } else {
      log(`${shapesCreatedCount}개의 도형을 자연어 배치에 맞게 로컬 빌드했습니다.`);
    }
  }
  
  progressBar.style.width = '100%';
  await sleep(100);
  
  focusCameraOnObject();
  updateStats();
  saveToGallery(prompt, 'local', '');
  log("3D 모델 로컬 생성이 완료되었습니다!", "success");
}

// Procedural presets (remain identical for local fallback)
function createSnowmanPreset() {
  const materials = {
    snow: new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.9, metalness: 0.1 }),
    carrot: new THREE.MeshStandardMaterial({ color: 0xff6b00, roughness: 0.5 }),
    coal: new THREE.MeshStandardMaterial({ color: 0x111111, roughness: 0.9 }),
  };
  const base = new THREE.Mesh(new THREE.SphereGeometry(1.2, 32, 32), materials.snow);
  base.position.y = 1.2; base.castShadow = true; base.receiveShadow = true;
  const mid = new THREE.Mesh(new THREE.SphereGeometry(0.8, 32, 32), materials.snow);
  mid.position.y = 2.8; mid.castShadow = true; mid.receiveShadow = true;
  const head = new THREE.Mesh(new THREE.SphereGeometry(0.5, 32, 32), materials.snow);
  head.position.y = 3.9; head.castShadow = true; head.receiveShadow = true;
  const nose = new THREE.Mesh(new THREE.ConeGeometry(0.1, 0.4, 16), materials.carrot);
  nose.rotation.x = Math.PI / 2; nose.position.set(0, 3.9, 0.6); nose.castShadow = true;
  const leftEye = new THREE.Mesh(new THREE.SphereGeometry(0.05, 8, 8), materials.coal);
  leftEye.position.set(0.18, 4.05, 0.44);
  const rightEye = new THREE.Mesh(new THREE.SphereGeometry(0.05, 8, 8), materials.coal);
  rightEye.position.set(-0.18, 4.05, 0.44);
  const btn1 = new THREE.Mesh(new THREE.SphereGeometry(0.07, 8, 8), materials.coal);
  btn1.position.set(0, 3.0, 0.77);
  const btn2 = new THREE.Mesh(new THREE.SphereGeometry(0.07, 8, 8), materials.coal);
  btn2.position.set(0, 2.7, 0.79);
  const hatColor = new THREE.MeshStandardMaterial({ color: 0x333333, roughness: 0.5, metalness: 0.1 });
  const hatBrim = new THREE.Mesh(new THREE.CylinderGeometry(0.6, 0.6, 0.05, 32), hatColor);
  hatBrim.position.y = 4.35; hatBrim.castShadow = true;
  const hatTop = new THREE.Mesh(new THREE.CylinderGeometry(0.4, 0.4, 0.6, 32), hatColor);
  hatTop.position.y = 4.65; hatTop.castShadow = true;
  currentModelGroup.add(base, mid, head, nose, leftEye, rightEye, btn1, btn2, hatBrim, hatTop);
}
function createTablePreset() {
  const woodMaterial = new THREE.MeshStandardMaterial({ color: 0x8b5a2b, roughness: 0.7, metalness: 0.1 });
  const legMaterial = new THREE.MeshStandardMaterial({ color: 0x222222, roughness: 0.5, metalness: 0.8 });
  const top = new THREE.Mesh(new THREE.BoxGeometry(4.0, 0.18, 2.5), woodMaterial);
  top.position.y = 1.8; top.castShadow = true; top.receiveShadow = true;
  const legGeo = new THREE.CylinderGeometry(0.08, 0.08, 1.7, 16);
  const legPositions = [{ x: 1.8, z: 1.0 }, { x: -1.8, z: 1.0 }, { x: 1.8, z: -1.0 }, { x: -1.8, z: -1.0 }];
  legPositions.forEach(pos => {
    const leg = new THREE.Mesh(legGeo, legMaterial);
    leg.position.set(pos.x, 0.85, pos.z); leg.castShadow = true; leg.receiveShadow = true;
    currentModelGroup.add(leg);
  });
  currentModelGroup.add(top);
}
function createChairPreset() {
  const fabricMaterial = new THREE.MeshStandardMaterial({ color: 0x334e68, roughness: 0.8 });
  const woodMaterial = new THREE.MeshStandardMaterial({ color: 0x6e4922, roughness: 0.6 });
  const seat = new THREE.Mesh(new THREE.BoxGeometry(1.6, 0.2, 1.6), fabricMaterial);
  seat.position.y = 1.1; seat.castShadow = true; seat.receiveShadow = true;
  const back = new THREE.Mesh(new THREE.BoxGeometry(1.6, 1.4, 0.2), fabricMaterial);
  back.position.set(0, 1.8, -0.7); back.castShadow = true; back.receiveShadow = true;
  const legGeo = new THREE.CylinderGeometry(0.07, 0.05, 1.0, 16);
  const legPos = [{ x: 0.7, z: 0.7 }, { x: -0.7, z: 0.7 }, { x: 0.7, z: -0.7 }, { x: -0.7, z: -0.7 }];
  legPos.forEach(pos => {
    const leg = new THREE.Mesh(legGeo, woodMaterial);
    leg.position.set(pos.x, 0.5, pos.z);
    leg.rotation.z = pos.x > 0 ? -0.08 : 0.08; leg.rotation.x = pos.z > 0 ? 0.08 : -0.08;
    leg.castShadow = true; leg.receiveShadow = true;
    currentModelGroup.add(leg);
  });
  currentModelGroup.add(seat, back);
}
function createTreePreset() {
  const trunkMaterial = new THREE.MeshStandardMaterial({ color: 0x5c4033, roughness: 0.9 });
  const foliageMaterial = new THREE.MeshStandardMaterial({ color: 0x2e8b57, roughness: 0.8 });
  const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.35, 2.0, 16), trunkMaterial);
  trunk.position.y = 1.0; trunk.castShadow = true; trunk.receiveShadow = true;
  currentModelGroup.add(trunk);
  const layerGeos = [new THREE.ConeGeometry(1.4, 1.6, 8), new THREE.ConeGeometry(1.1, 1.4, 8), new THREE.ConeGeometry(0.8, 1.2, 8)];
  const layerHeights = [2.5, 3.4, 4.2];
  layerGeos.forEach((geo, idx) => {
    const layer = new THREE.Mesh(geo, foliageMaterial);
    layer.position.y = layerHeights[idx]; layer.castShadow = true; layer.receiveShadow = true;
    currentModelGroup.add(layer);
  });
}
function createSwordPreset() {
  const steelMat = new THREE.MeshStandardMaterial({ color: 0xb0c4de, roughness: 0.2, metalness: 0.95 });
  const goldMat = new THREE.MeshStandardMaterial({ color: 0xffd700, roughness: 0.3, metalness: 0.9 });
  const gripMat = new THREE.MeshStandardMaterial({ color: 0x3d2314, roughness: 0.8 });
  const blade = new THREE.Mesh(new THREE.BoxGeometry(0.25, 3.2, 0.06), steelMat);
  blade.position.y = 2.8; blade.castShadow = true;
  const tip = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.4, 4), steelMat);
  tip.rotation.y = Math.PI / 4; tip.position.y = 4.6; tip.scale.set(1.4, 1, 0.4); tip.castShadow = true;
  const guard = new THREE.Mesh(new THREE.TorusGeometry(0.4, 0.08, 12, 24), goldMat);
  guard.position.y = 1.2; guard.rotation.x = Math.PI / 2; guard.scale.set(1.5, 0.8, 1.0); guard.castShadow = true;
  const handle = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 0.9, 16), gripMat);
  handle.position.y = 0.75; handle.castShadow = true;
  const pommel = new THREE.Mesh(new THREE.SphereGeometry(0.14, 16, 16), goldMat);
  pommel.position.y = 0.25; pommel.castShadow = true;
  currentModelGroup.add(blade, tip, guard, handle, pommel);
  currentModelGroup.rotation.z = -Math.PI / 6; currentModelGroup.position.y = 0.5;
}
function createHousePreset() {
  const wallMat = new THREE.MeshStandardMaterial({ color: 0xeeddcc, roughness: 0.8 });
  const roofMat = new THREE.MeshStandardMaterial({ color: 0xd63031, roughness: 0.6 });
  const doorMat = new THREE.MeshStandardMaterial({ color: 0x5c4033, roughness: 0.9 });
  const windowMat = new THREE.MeshStandardMaterial({ color: 0x74b9ff, roughness: 0.1, metalness: 0.9 });
  const walls = new THREE.Mesh(new THREE.BoxGeometry(2.5, 2.0, 2.5), wallMat);
  walls.position.y = 1.0; walls.castShadow = true; walls.receiveShadow = true;
  const roof = new THREE.Mesh(new THREE.ConeGeometry(2.1, 1.4, 4), roofMat);
  roof.rotation.y = Math.PI / 4; roof.position.y = 2.7; roof.castShadow = true;
  const door = new THREE.Mesh(new THREE.BoxGeometry(0.6, 1.2, 0.05), doorMat);
  door.position.set(0, 0.6, 1.25); door.castShadow = true;
  const win1 = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.5, 0.05), windowMat);
  win1.position.set(0.7, 1.2, 1.25); win1.castShadow = true;
  const win2 = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.5, 0.05), windowMat);
  win2.position.set(-0.7, 1.2, 1.25); win2.castShadow = true;
  currentModelGroup.add(walls, roof, door, win1, win2);
}
function createDefaultShowcase() {
  const materials = [
    new THREE.MeshStandardMaterial({ color: 0xff3b30, roughness: 0.2, metalness: 0.5 }),
    new THREE.MeshStandardMaterial({ color: 0x007aff, roughness: 0.8, metalness: 0.1 }),
    new THREE.MeshStandardMaterial({ color: 0x34c759, roughness: 0.3, metalness: 0.3 }),
    new THREE.MeshStandardMaterial({ color: 0xffcc00, roughness: 0.1, metalness: 0.9 })
  ];
  const box = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), materials[0]);
  box.position.set(-1.2, 0.5, 0); box.castShadow = true; box.receiveShadow = true;
  const sphere = new THREE.Mesh(new THREE.SphereGeometry(0.6, 32, 32), materials[1]);
  sphere.position.set(1.2, 0.6, 0); sphere.castShadow = true; sphere.receiveShadow = true;
  const cyl = new THREE.Mesh(new THREE.CylinderGeometry(0.4, 0.4, 1.2, 32), materials[2]);
  cyl.position.set(0, 0.6, 1.2); cyl.castShadow = true; cyl.receiveShadow = true;
  const torus = new THREE.Mesh(new THREE.TorusGeometry(0.5, 0.15, 16, 100), materials[3]);
  torus.position.set(0, 0.2, -1.2); torus.rotation.x = Math.PI / 2; torus.castShadow = true;
  const cone = new THREE.Mesh(new THREE.ConeGeometry(0.3, 0.8, 32), materials[0]);
  cone.position.set(0, 0.7, -1.2); cone.castShadow = true;
  currentModelGroup.add(box, sphere, cyl, torus, cone);
}

// -------------------------------------------------------------
// 2. THREE.JS AI CODE GENERATOR (THREE AI MODE)
// -------------------------------------------------------------
async function generateModelThreeAI(prompt, progressBar) {
  if (!geminiApiKey) {
    log("설정(Settings) 탭에서 Gemini API Key를 입력해 주세요.", "error");
    progressBar.style.width = '0%';
    alert("Three AI 모드를 사용하려면 Gemini API Key가 필요합니다.");
    return;
  }
  
  log("Gemini API Three.js 코드 요청 중...");
  progressBar.style.width = '30%';
  
  const systemPrompt = `You are a Three.js expert developer. Your task is to output valid Javascript code to programmatically build a 3D model group based on a user's natural language request.

CRITICAL INSTRUCTIONS:
1. Return ONLY executable JavaScript code. Do NOT wrap your code in html templates or markdown text OTHER than standard \`\`\`javascript ... \`\`\` code block.
2. The user has a global \`THREE\` object, and a pre-defined container \`modelGroup\` (which is a \`THREE.Group\`).
3. You must add all generated meshes, groups, and lights directly to \`modelGroup\`. Example: \`modelGroup.add(mesh);\`. Do not create a new Scene, Renderer, Camera, or start requestAnimationFrame.
4. Try to make the design rich, creative, and detailed:
   - Use multiple components with proper dimensions, positions, and rotations to make a realistic/aesthetic representation of: "${prompt}".
   - Choose appropriate colors, roughness, and metalness (e.g. metals get high metalness, fabrics/organic get high roughness). Use \`THREE.MeshStandardMaterial\` for modern look.
   - For all created meshes, set: \`mesh.castShadow = true; mesh.receiveShadow = true;\` so shadows look spectacular.
5. Create a clean hierarchy. You can group objects with \`new THREE.Group()\` and add them to \`modelGroup\`.
6. Make sure to define proper shapes using geometries like \`THREE.BoxGeometry\`, \`THREE.SphereGeometry\`, \`THREE.CylinderGeometry\`, \`THREE.ConeGeometry\`, \`THREE.TorusGeometry\`, \`THREE.CapsuleGeometry\`.
7. Ground position: The ground grid is at y = 0. Align your shapes so the base of the model stands roughly at y = 0 (e.g., if a box height is 2, place it at y = 1 so its bottom is on the ground).
8. Avoid complex external assets or texture loaders. Use procedural geometries, vertex coloring, or simple materials.
9. Keep it self-contained and syntax-error free. Do not write explainers or HTML tags outside the code block.

Generate detailed Three.js code for: "${prompt}"`;

  try {
    const code = await fetchGeminiCode(systemPrompt, 'javascript');
    progressBar.style.width = '70%';
    
    // Display code
    showCodeContainer("Three.js 스크립트 코드", code, false);
    
    log("샌드박스 컴파일 및 실행 중...");
    progressBar.style.width = '85%';
    
    const sandboxFn = new Function('THREE', 'modelGroup', code);
    sandboxFn(THREE, currentModelGroup);
    
    if (wireframeMode) {
      currentModelGroup.traverse(node => {
        if (node.isMesh && node.material) node.material.wireframe = true;
      });
    }
    
    progressBar.style.width = '100%';
    await sleep(150);
    
    focusCameraOnObject();
    updateStats();
    saveToGallery(prompt, 'three', code);
    log("3D 모델 Three.js AI 생성이 완료되었습니다!", "success");
    
  } catch (err) {
    log(`실행 오류: ${err.message}`, 'error');
    console.error(err);
    log("에러 발생으로 로컬 모드로 자동 대체 생성합니다.");
    await generateModelLocally(prompt, progressBar);
  }
}

// -------------------------------------------------------------
// 3. BLENDER PYTHON AI GENERATOR (BLENDER AI MODE)
// -------------------------------------------------------------
async function generateModelBlenderAI(prompt, progressBar) {
  if (!geminiApiKey) {
    log("설정(Settings) 탭에서 Gemini API Key를 입력해 주세요.", "error");
    progressBar.style.width = '0%';
    alert("Blender AI 모드를 사용하려면 Gemini API Key가 필요합니다.");
    return;
  }
  
  log("Gemini API 블렌더 파이썬 스크립트 요청 중...");
  progressBar.style.width = '30%';
  
  const systemPrompt = `You are a Blender Python API (bpy) expert developer. Your task is to output valid Python code for Blender to programmatically build a detailed, aesthetic 3D model based on the user's natural language request.

CRITICAL INSTRUCTIONS:
1. Return ONLY executable Python script. Do NOT wrap your code in html templates or markdown text OTHER than standard \`\`\`python ... \`\`\` code block.
2. Do NOT import bpy and clear the scene yourself; we already prepend a cleanup block that deletes all objects.
3. Do NOT write GLTF export code; we already append an export block at the end of the script to save the file.
4. Focus entirely on mesh generation, operations, and materials:
   - Use bpy.ops.mesh to add primitives (primitive_cube_add, primitive_cylinder_add, primitive_cone_add, primitive_torus_add, primitive_uv_sphere_add, etc.).
   - Position objects precisely by setting obj.location = (x, y, z).
   - Scale objects by setting obj.scale = (x, y, z).
   - Add modifier if needed to smooth (e.g. Subdivision Surface, Bevel).
   - Create materials, define color (diffuse_color or use nodes), and link them to objects.
   - Create a rich, detailed, and creative assembly for: "${prompt}".
5. Ground position: The grid is at z = 0. Align shapes so they stand on the grid (e.g. z >= 0). Note that Blender uses Z-up! (X-right, Y-forward, Z-up).
6. Keep the script self-contained and syntax-error free. No explanations.

Generate detailed Blender Python code for: "${prompt}"`;

  try {
    const code = await fetchGeminiCode(systemPrompt, 'python');
    progressBar.style.width = '70%';
    
    // Display code and show run button
    showCodeContainer("블렌더 파이썬 스크립트", code, true);
    
    log("블렌더 스크립트 작성이 완료되었습니다.");
    
    // If Socket mode is enabled or Blender path is configured, trigger automatic build
    if (socketMode || blenderPath) {
      log(socketMode ? "실시간 소켓을 통해 블렌더 자동 빌드를 시작합니다..." : "설정된 블렌더 실행 파일을 사용해 자동 빌드를 진행합니다...");
      await runBlenderLocalProcess(progressBar);
    } else {
      progressBar.style.width = '100%';
      log("블렌더 연동 경로가 설정되지 않았고 소켓 모드도 비활성화되어 수동 가이드를 표시합니다.", "error");
    }
    
    saveToGallery(prompt, 'blender', code);
    
  } catch (err) {
    log(`스크립트 생성 실패: ${err.message}`, 'error');
    console.error(err);
    log("에러 발생으로 로컬 엔진으로 대체 생성합니다.");
    await generateModelLocally(prompt, progressBar);
  }
}

// Fetch generated script from Gemini API
async function fetchGeminiCode(systemPrompt, langKey) {
  const response = await fetch(`https://generativelanguage.googleapis.com/v1/models/${geminiModelName}:generateContent?key=${geminiApiKey}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      contents: [{
        parts: [{ text: systemPrompt }]
      }],
      generationConfig: {
        temperature: 0.3
      }
    })
  });
  
  if (!response.ok) {
    const errData = await response.json();
    if (response.status === 404 || (errData.error && errData.error.message && errData.error.message.includes("not found"))) {
      log("모델을 찾을 수 없어 사용 가능한 API 모델 목록을 조회합니다...", "error");
      listAvailableModels();
    }
    throw new Error(errData.error?.message || `HTTP ${response.status}`);
  }
  
  const data = await response.json();
  const responseText = data.candidates?.[0]?.content?.parts?.[0]?.text;
  
  if (!responseText) {
    throw new Error("API가 빈 응답을 반환했습니다.");
  }
  
  // Extract code block
  let code = "";
  const regex = new RegExp(`\`\`\`(?:${langKey})?([\\s\\S]*?)\`\`\``, "i");
  const match = responseText.match(regex);
  
  if (match && match[1]) {
    code = match[1].trim();
  } else {
    code = responseText.trim();
  }
  
  return code;
}

// Support function: List all models linked to the key
async function listAvailableModels() {
  if (!geminiApiKey) return;
  try {
    const response = await fetch(`https://generativelanguage.googleapis.com/v1/models?key=${geminiApiKey}`);
    if (response.ok) {
      const data = await response.json();
      const models = data.models.map(m => m.name.replace('models/', ''));
      log("사용 가능한 API 모델: " + models.join(', '), "success");
    } else {
      const responseBeta = await fetch(`https://generativelanguage.googleapis.com/v1beta/models?key=${geminiApiKey}`);
      if (responseBeta.ok) {
        const dataBeta = await responseBeta.json();
        const modelsBeta = dataBeta.models.map(m => m.name.replace('models/', ''));
        log("사용 가능한 API 모델(Beta): " + modelsBeta.join(', '), "success");
      } else {
        log("API 키가 유효하지 않거나 모델 목록을 불러올 수 없습니다. API 키를 재확인해 주세요.", "error");
      }
    }
  } catch (e) {
    log("모델 목록 조회 통신 에러: " + e.message, "error");
  }
}

// -------------------------------------------------------------
// 4. LOCAL BLENDER PROCESS RUNNER & GLTF LOADER
// -------------------------------------------------------------
async function runBlenderLocalProcess(customProgressBar = null) {
  const progressBar = customProgressBar || document.getElementById('progress-bar');
  const statusPanel = document.getElementById('status-panel');
  statusPanel.classList.remove('hidden');
  
  if (!generatedScriptCode) {
    alert("실행할 파이썬 코드가 존재하지 않습니다.");
    return;
  }
  
  log("백엔드에 블렌더 빌드 요청 전송 중...");
  progressBar.style.width = '75%';
  
  try {
    const response = await fetch(getApiUrl('/api/run-blender'), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        code: generatedScriptCode,
        blenderPath: blenderPath,
        socketMode: socketMode,
        socketPort: socketPort
      })
    });
    
    const data = await response.json();
    
    if (!response.ok || !data.success) {
      throw new Error(data.error || '블렌더 빌드 서버 에러');
    }
    
    log("블렌더 3D 모델 렌더링 완료! GLTF 로딩 중...");
    progressBar.style.width = '90%';
    
    // Load generated GLTF file into Three.js
    const loader = new THREE.GLTFLoader();
    loader.load(data.fileUrl, (gltf) => {
      clearModelGroup();
      
      const importedScene = gltf.scene;
      
      // Make sure shadow features are enabled
      importedScene.traverse(node => {
        if (node.isMesh) {
          node.castShadow = true;
          node.receiveShadow = true;
          // Apply wireframe if enabled
          if (wireframeMode && node.material) {
            node.material.wireframe = true;
          }
        }
      });
      
      // Adjust Blender scale/rotation (Blender is Z-up, Three.js is Y-up)
      // Usually GLTFLoader automatically handles this, but if not we can adjust.
      // Blender Z-up translates cleanly inside GLTF export.
      
      currentModelGroup.add(importedScene);
      
      progressBar.style.width = '100%';
      focusCameraOnObject();
      updateStats();
      
      log("블렌더 3D 모델 자동 연동 및 웹 뷰어 투영 성공!", "success");
      
    }, undefined, (loadErr) => {
      log(`GLTF 로딩 실패: ${loadErr.message}`, 'error');
    });
    
  } catch (err) {
    log(`블렌더 실행 오류: ${err.message}`, 'error');
    document.getElementById('blender-guide-panel').classList.remove('hidden');
    progressBar.style.width = '100%';
  }
}

// Automatically launch Blender GUI and host the Socket Server script
async function launchBlenderGUI() {
  if (!blenderPath) {
    log("블렌더 실행 파일 경로가 설정되어 있지 않습니다. 설정 탭으로 이동합니다.", "error");
    alert("설정 탭에서 Blender.exe 설치 경로를 먼저 입력해 주세요.");
    // Switch to Settings tab
    const settingsTabBtn = document.getElementById('tab-btn-settings');
    if (settingsTabBtn) settingsTabBtn.click();
    return;
  }

  log("블렌더 자동 기동 및 소켓 서버 활성화 요청 중...");
  
  try {
    const response = await fetch(getApiUrl('/api/launch-blender'), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        blenderPath: blenderPath
      })
    });

    const data = await response.json();

    if (!response.ok || !data.success) {
      throw new Error(data.error || '블렌더 실행 서버 에러');
    }

    log(`성공: ${data.message}`, "success");
    alert(data.message);
  } catch (err) {
    log(`블렌더 실행 오류: ${err.message}`, "error");
    alert(`블렌더 실행 실패: ${err.message}`);
  }
}

// -------------------------------------------------------------
// 5. EXPORT & DOWNLOAD UTILS
// -------------------------------------------------------------
function export3DModel(format) {
  if (currentModelGroup.children.length === 0) {
    alert("내보낼 3D 모델이 없습니다. 먼저 모델을 생성해 주세요.");
    return;
  }
  
  log(`${format.toUpperCase()} 파일로 내보내기 변환 중...`);
  const originalRotation = currentModelGroup.rotation.y;
  currentModelGroup.rotation.y = 0;
  
  try {
    if (format === 'gltf') {
      const exporter = new THREE.GLTFExporter();
      exporter.parse(currentModelGroup, (gltf) => {
        const output = JSON.stringify(gltf, null, 2);
        downloadFile(output, 'model.gltf', 'application/json');
        log("GLTF 모델 다운로드 완료!", "success");
      }, (err) => {
        log(`GLTF 변환 실패: ${err.message}`, 'error');
      }, { binary: false });
      
    } else if (format === 'obj') {
      const exporter = new THREE.OBJExporter();
      const output = exporter.parse(currentModelGroup);
      downloadFile(output, 'model.obj', 'text/plain');
      log("OBJ 모델 다운로드 완료!", "success");
      
    } else if (format === 'stl') {
      const exporter = new THREE.STLExporter();
      const output = exporter.parse(currentModelGroup, { binary: true });
      const blob = new Blob([output], { type: 'application/octet-stream' });
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = 'model.stl';
      link.click();
      log("STL 모델 다운로드 완료!", "success");
    }
  } catch (err) {
    log(`내보내기 오류: ${err.message}`, 'error');
  } finally {
    currentModelGroup.rotation.y = originalRotation;
  }
}

function downloadFile(content, fileName, contentType) {
  const a = document.createElement("a");
  const file = new Blob([content], { type: contentType });
  a.href = URL.createObjectURL(file);
  a.download = fileName;
  a.click();
}

// -------------------------------------------------------------
// 6. STORAGE & GALLERY MANAGER
// -------------------------------------------------------------
function saveToGallery(prompt, mode, code) {
  let gallery = JSON.parse(localStorage.getItem('aura3d_gallery')) || [];
  gallery = gallery.filter(item => item.prompt !== prompt);
  
  const newItem = {
    id: Date.now().toString(),
    prompt: prompt,
    mode: mode,
    code: code,
    date: new Date().toLocaleDateString('ko-KR')
  };
  
  gallery.unshift(newItem);
  if (gallery.length > 30) gallery.pop();
  
  localStorage.setItem('aura3d_gallery', JSON.stringify(gallery));
  renderGallery();
}

function renderGallery() {
  const galleryList = document.getElementById('gallery-list');
  const gallery = JSON.parse(localStorage.getItem('aura3d_gallery')) || [];
  
  if (gallery.length === 0) {
    galleryList.innerHTML = `
      <div class="empty-gallery">
        <span class="material-symbols-outlined">inventory_2</span>
        <p>생성된 3D 모델이 없습니다.<br>첫 모델을 만들어 보세요!</p>
      </div>
    `;
    return;
  }
  
  galleryList.innerHTML = '';
  
  gallery.forEach(item => {
    const card = document.createElement('div');
    card.className = 'gallery-item';
    
    let engineLabel = '로컬';
    if (item.mode === 'three') engineLabel = 'Three AI';
    if (item.mode === 'blender') engineLabel = 'Blender AI';
    
    card.innerHTML = `
      <div class="gallery-item-details">
        <span class="gallery-item-prompt" title="${item.prompt}">${item.prompt}</span>
        <div class="gallery-item-meta">
          <span>${item.date}</span>
          <span>•</span>
          <span>${engineLabel} 엔진</span>
        </div>
      </div>
      <button class="gallery-item-delete" data-id="${item.id}" title="기록 삭제">
        <span class="material-symbols-outlined">delete</span>
      </button>
    `;
    
    card.addEventListener('click', (e) => {
      if (e.target.closest('.gallery-item-delete')) return;
      
      document.getElementById('prompt-input').value = item.prompt;
      
      currentEngineMode = item.mode;
      const modeBtnLocal = document.getElementById('mode-btn-local');
      const modeBtnThree = document.getElementById('mode-btn-three');
      const modeBtnBlender = document.getElementById('mode-btn-blender');
      
      [modeBtnLocal, modeBtnThree, modeBtnBlender].forEach(b => b.classList.remove('active'));
      
      if (item.mode === 'three') {
        modeBtnThree.classList.add('active');
        document.getElementById('stat-engine').textContent = 'Three AI';
        if (item.code) showCodeContainer("Three.js 스크립트 코드", item.code, false);
      } else if (item.mode === 'blender') {
        modeBtnBlender.classList.add('active');
        document.getElementById('stat-engine').textContent = 'Blender AI';
        if (item.code) showCodeContainer("블렌더 파이썬 스크립트", item.code, true);
      } else {
        modeBtnLocal.classList.add('active');
        document.getElementById('stat-engine').textContent = '로컬 엔진';
        hideCodeContainer();
      }
      
      generateModel();
    });
    
    const deleteBtn = card.querySelector('.gallery-item-delete');
    deleteBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      deleteGalleryItem(item.id);
    });
    
    galleryList.appendChild(card);
  });
}

function deleteGalleryItem(id) {
  let gallery = JSON.parse(localStorage.getItem('aura3d_gallery')) || [];
  gallery = gallery.filter(item => item.id !== id);
  localStorage.setItem('aura3d_gallery', JSON.stringify(gallery));
  renderGallery();
  log("기록이 삭제되었습니다.");
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

// Get API absolute/relative URL based on backendUrl configuration
function getApiUrl(path) {
  if (!backendUrl) {
    return path;
  }
  const base = backendUrl.replace(/\/+$/, '');
  const cleanPath = '/' + path.replace(/^\/+/, '');
  return base + cleanPath;
}
