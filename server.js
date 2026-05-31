const express = require('express');
const path = require('path');
const fs = require('fs');
const net = require('net');
const { exec, spawn } = require('child_process');

const app = express();
const PORT = 8080;

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

// Start Server
app.listen(PORT, () => {
  console.log(`==================================================`);
  console.log(` Aura3D Backend Server running at http://localhost:${PORT}`);
  console.log(`==================================================`);
});
