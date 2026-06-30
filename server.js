const express = require('express');
const path = require('path');
const fs = require('fs');
const net = require('net');
const { exec, spawn } = require('child_process');
const multer = require('multer');

const app = express();
const PORT = 8080;

// Start resident Python CAD Daemon Server automatically
let daemonProcess = null;
function startCADDaemon() {
  console.log('[System] Launching resident Python CAD Daemon on port 8081...');
  daemonProcess = spawn('python', ['step_parser_daemon.py']);
  
  daemonProcess.stdout.on('data', (data) => {
    console.log(`[Daemon STDOUT] ${data.toString().trim()}`);
  });
  
  daemonProcess.stderr.on('data', (data) => {
    console.error(`[Daemon STDERR] ${data.toString().trim()}`);
  });
  
  daemonProcess.on('close', (code) => {
    console.log(`[Daemon] Process exited with code ${code}`);
  });
}
startCADDaemon();

// Clean up daemon process on server shutdown
process.on('exit', () => {
  if (daemonProcess) daemonProcess.kill();
});
process.on('SIGINT', () => {
  if (daemonProcess) daemonProcess.kill();
  process.exit();
});
process.on('SIGTERM', () => {
  if (daemonProcess) daemonProcess.kill();
  process.exit();
});

app.use((req, res, next) => {
  res.header('Access-Control-Allow-Origin', '*');
  res.header('Access-Control-Allow-Headers', 'Origin, X-Requested-With, Content-Type, Accept, Authorization, ngrok-skip-browser-warning, bypass-tunnel-reminder');
  res.header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS, PUT, DELETE');
  if (req.method === 'OPTIONS') {
    return res.sendStatus(200);
  }
  next();
});

app.use(express.json());
app.use(express.static(path.join(__dirname)));

const scratchDir = path.join(__dirname, 'scratch');
if (!fs.existsSync(scratchDir)) {
  fs.mkdirSync(scratchDir);
}

app.use('/scratch', express.static(scratchDir));

/**
 * Helper: Runs Blender in background CLI mode (Fallback)
 */
function runBlenderCLI(blenderPath, scriptPath, outputPath, res) {
  const cmd = `"${blenderPath}" --background --python "${scriptPath}"`;
  console.log(`[Blender CLI Fallback] Running: ${cmd}`);

  exec(cmd, (error, stdout, stderr) => {
    // Write run logs
    const logPath = path.join(scratchDir, 'blender_run.log');
    const logContent = `CLI FALLBACK RUN\nCOMMAND: ${cmd}\n\nSTDOUT:\n${stdout}\n\nSTDERR:\n${stderr}\n\nERROR:\n${error ? error.message : 'None'}`;
    fs.writeFileSync(logPath, logContent, 'utf8');

    if (error) {
      console.error(`[Blender CLI Error]`, error.message);
      return res.status(500).json({
        success: false,
        error: `블렌더 실행 실패. 경로를 다시 확인하거나 블렌더에서 소켓 서버를 켜주세요.`,
        details: stderr || error.message,
        stdout: stdout
      });
    }

    if (!fs.existsSync(outputPath)) {
      return res.status(500).json({
        success: false,
        error: '블렌더 CLI가 실행되었으나 output.glb 파일 생성에 실패했습니다. (블렌더 로그 참조)',
        stdout: stdout
      });
    }

    return res.json({
      success: true,
      fileUrl: '/scratch/output.glb?t=' + Date.now(),
      method: 'cli',
      stdout: stdout
    });
  });
}

/**
 * API Endpoint: Execute Blender script
 * Tries Socket connection first (Real-time), falls back to CLI (Background) if offline.
 */
app.post('/api/run-blender', (req, res) => {
  const { code, blenderPath, socketMode, socketPort } = req.body;
  
  if (!code) {
    return res.status(400).json({ success: false, error: '파이썬 코드가 제공되지 않았습니다.' });
  }

  const finalBlenderPath = blenderPath ? blenderPath.trim() : 'blender';
  const port = socketPort ? parseInt(socketPort) : 5555;
  const scriptPath = path.join(scratchDir, 'temp_blender_script.py');
  const outputPath = path.join(scratchDir, 'output.glb');

  // Prepend cleanup and append GLTF export to make standalone Python scripts safe for CLI execution
  const prependedCode = `import bpy
import os

try:
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
except Exception as e:
    print("Cleanup error:", str(e))
\n`;

  // Blender 4.0+ and 5.0+ deprecate GLTF_EMBEDDED, format GLB is standard
  const appendedCode = `\n
try:
    export_path = r"${outputPath.replace(/\\/g, '\\\\')}"
    os.makedirs(os.path.dirname(export_path), exist_ok=True)
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.gltf(
        filepath=export_path,
        export_format='GLB',
        use_selection=False
    )
    print("SUCCESS")
except Exception as e:
    print("ERROR:", str(e))
`;

  // Always write script to file for potential CLI fallback or user inspection
  const fullPythonScript = prependedCode + code + appendedCode;
  fs.writeFileSync(scriptPath, fullPythonScript, 'utf8');

  // 1. If socket mode is activated, try socket communication first
  if (socketMode) {
    console.log(`[Blender Socket] Connecting to active Blender session on localhost:${port}...`);
    
    const client = new net.Socket();
    client.setTimeout(2500); // Connection timeout (2.5 seconds)
    
    let responseData = "";

    client.connect(port, '127.0.0.1', () => {
      console.log(`[Blender Socket] Connected! Sending script payload...`);
      // Send code and outputPath as JSON
      const payload = {
        code: code,
        outputPath: outputPath
      };
      client.write(JSON.stringify(payload));
      client.end(); // Tell server we've finished sending
    });

    client.on('data', (chunk) => {
      responseData += chunk.toString();
    });

    client.on('end', () => {
      console.log(`[Blender Socket] Response received: ${responseData.trim()}`);
      
      if (responseData.startsWith("SUCCESS")) {
        return res.json({
          success: true,
          fileUrl: '/scratch/output.glb?t=' + Date.now(),
          method: 'socket'
        });
      } else {
        const errorMsg = responseData.replace("ERROR: ", "").trim();
        return res.status(500).json({
          success: false,
          error: `블렌더 내 스크립트 실행 오류: ${errorMsg}`,
          details: responseData
        });
      }
    });

    client.on('timeout', () => {
      console.warn(`[Blender Socket] Connection timed out. Falling back to CLI...`);
      client.destroy();
      runBlenderCLI(finalBlenderPath, scriptPath, outputPath, res);
    });

    client.on('error', (err) => {
      console.warn(`[Blender Socket] Socket connection failed (${err.message}). Falling back to CLI mode...`);
      client.destroy();
      // Automatic fallback to background CLI
      runBlenderCLI(finalBlenderPath, scriptPath, outputPath, res);
    });

  } else {
    // 2. Socket mode disabled: direct run via CLI
    runBlenderCLI(finalBlenderPath, scriptPath, outputPath, res);
  }
});

/**
 * API Endpoint: Automatically launch Blender in GUI mode with socket server script pre-loaded
 */
app.post('/api/launch-blender', (req, res) => {
  const { blenderPath } = req.body;

  if (!blenderPath) {
    return res.status(400).json({ success: false, error: '설정 탭에서 블렌더 실행 파일 경로를 먼저 입력해 주세요.' });
  }

  if (!fs.existsSync(blenderPath)) {
    return res.status(400).json({ success: false, error: `입력된 경로에 블렌더 실행 파일이 존재하지 않습니다:\n${blenderPath}` });
  }

  const serverScriptPath = path.join(__dirname, 'blender_socket_server.py');
  
  if (!fs.existsSync(serverScriptPath)) {
    return res.status(500).json({ success: false, error: '서버 연동용 blender_socket_server.py 파일이 프로젝트 루트에 존재하지 않습니다.' });
  }

  try {
    console.log(`[Blender Launch] Automatically spawning Blender GUI with script: "${blenderPath}" --python "${serverScriptPath}"`);
    
    // Spawn Blender GUI process detached from Node so it remains running even if Node restarts
    const child = spawn(blenderPath, ['--python', serverScriptPath], {
      detached: true,
      stdio: 'ignore'
    });
    
    child.unref(); // Prevent parent node process from waiting for child to exit

    return res.json({
      success: true,
      message: '블렌더 프로그램이 실행되었습니다. 곧 소켓 서버가 활성화됩니다.'
    });
  } catch (err) {
    console.error(`[Blender Launch Error]`, err.message);
    return res.status(500).json({
      success: false,
      error: `블렌더 실행 도중 오류가 발생했습니다: ${err.message}`
    });
  }
});

// Configure Multer for temp CAD storage
const storage = multer.diskStorage({
  destination: function (req, file, cb) {
    const uploadTempDir = path.join(scratchDir, 'uploads');
    if (!fs.existsSync(uploadTempDir)) {
      fs.mkdirSync(uploadTempDir, { recursive: true });
    }
    cb(null, uploadTempDir);
  },
  filename: function (req, file, cb) {
    const uniqueSuffix = Date.now() + '-' + Math.round(Math.random() * 1E9);
    cb(null, uniqueSuffix + path.extname(file.originalname));
  }
});
const upload = multer({ storage: storage });

/**
 * API Endpoint: Upload and Parse STEP/STP robot assembly (Uses Daemon)
 */
app.post('/api/upload-step', upload.single('step_file'), async (req, res) => {
  if (!req.file) {
    return res.status(400).json({ success: false, error: '파일이 제공되지 않았습니다.' });
  }

  const stepFilePath = req.file.path;
  const conversionId = 'conv_' + Date.now() + '_' + Math.random().toString(36).substring(2, 7);
  const conversionDir = path.join(scratchDir, 'conversions', conversionId);
  fs.mkdirSync(conversionDir, { recursive: true });

  try {
    // Read STEP file bytes and encode to base64
    const stepBytes = fs.readFileSync(stepFilePath);
    const stepBase64 = stepBytes.toString('base64');
    
    // Call resident python daemon server
    const response = await fetch('http://localhost:8081/parse', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        step_data: stepBase64,
        output_dir: conversionDir,
        filename: req.file.originalname
      })
    });
    
    // Clean up temp uploaded file
    try { fs.unlinkSync(stepFilePath); } catch (e) {}
    
    if (!response.ok) {
      const errData = await response.json().catch(() => ({}));
      return res.status(500).json({
        success: false,
        error: errData.error || '데몬 파서 서버에서 오류가 발생했습니다.'
      });
    }
    
    const resData = await response.json();
    return res.json({
      success: true,
      conversionId: conversionId,
      data: resData.data
    });
    
  } catch (error) {
    try { fs.unlinkSync(stepFilePath); } catch (e) {}
    console.error(`[Upload Error]`, error.message);
    return res.status(500).json({
      success: false,
      error: `데몬 연동 분석 실패: ${error.message}`
    });
  }
});

/**
 * API Endpoint: Edit kinematics and Export robot (Uses Daemon)
 */
app.post('/api/export-robot', async (req, res) => {
  const { conversionId, config } = req.body;

  if (!conversionId || !config) {
    return res.status(400).json({ success: false, error: 'conversionId 또는 config 데이터가 누락되었습니다.' });
  }

  const conversionDir = path.join(scratchDir, 'conversions', conversionId);
  if (!fs.existsSync(conversionDir)) {
    return res.status(400).json({ success: false, error: '유효하지 않거나 만료된 세션(conversionId)입니다.' });
  }

  try {
    const response = await fetch('http://localhost:8081/export', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        config: config,
        output_dir: conversionDir
      })
    });
    
    if (!response.ok) {
      const errData = await response.json().catch(() => ({}));
      return res.status(500).json({
        success: false,
        error: errData.error || '데몬 익스포터 서버에서 오류가 발생했습니다.'
      });
    }
    
    const resData = await response.json();
    const zipFilename = resData.zip_filename;
    
    return res.json({
      success: true,
      zipUrl: `/scratch/conversions/${zipFilename}?t=` + Date.now(),
      message: '로봇 모델 패키지(URDF & USD) 변환 성공!'
    });
    
  } catch (error) {
    console.error(`[Export Error]`, error.message);
    return res.status(500).json({
      success: false,
      error: `데몬 연동 내보내기 실패: ${error.message}`
    });
  }
});

// Start Server
app.listen(PORT, () => {
  console.log(`==================================================`);
  console.log(` Aura3D Backend Server running at http://localhost:${PORT}`);
  console.log(`==================================================`);
});
