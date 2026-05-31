# Blender Real-time Socket Server for Aura3D
#
# How to run:
# 1. Open Blender.
# 2. Go to the "Scripting" workspace at the top menu.
# 3. Click "New" to create a new text file.
# 4. Copy and paste this script into the text area.
# 5. Click the "Run Script" (Play ▶) button.
# 6. Look at the System Console (Window -> Toggle System Console) to verify the server is running on port 5555.
#
# Keep Blender open while using Aura3D!

import bpy
import socket
import threading
import queue
import json
import os
import sys

PORT = 5555
execution_queue = queue.Queue()
server_running = True
server_thread = None

print("\n" + "="*50)
print(f" Aura3D Blender Socket Server initializing on port {PORT}...")
print("="*50)

def run_script_in_blender_main_thread(payload, result_dict, done_event):
    """
    Executes the Python script in the Blender main thread and exports scene to GLTF.
    """
    try:
        code = payload.get("code", "")
        output_path = payload.get("outputPath", "")
        
        # 1. Clear current Blender scene
        print("[Aura3D] Clearing active Blender scene...")
        try:
            bpy.ops.object.select_all(action='SELECT')
            bpy.ops.object.delete(use_global=False)
        except Exception as cle:
            print("[Aura3D] Scene cleanup error:", str(cle))
            
        # Ensure we have bpy imported in execution context
        exec_globals = {
            'bpy': bpy,
            '__builtins__': __builtins__,
            'print': print
        }
        
        # 2. Execute generated script code
        print("[Aura3D] Running AI script code...")
        exec(code, exec_globals)
        
        # 3. Auto Export to GLTF
        if output_path:
            print(f"[Aura3D] Exporting scene to: {output_path}")
            os.makedirs(os.path.dirname(output_path), exist_ok=True)
            # Select all objects to make sure they are included in GLTF
            bpy.ops.object.select_all(action='SELECT')
            
            # Export operation
            bpy.ops.export_scene.gltf(
                filepath=output_path,
                export_format='GLB',
                use_selection=False
            )
            print("[Aura3D] Successfully exported GLTF!")
            
        result_dict["status"] = "success"
    except Exception as e:
        print("[Aura3D] Error executing script:", str(e))
        result_dict["status"] = "error"
        result_dict["error"] = str(e)
    finally:
        # Notify the waiting socket thread that execution is complete
        done_event.set()

def blender_queue_timer_poller():
    """
    Blender App Timer function. Runs on the Blender main thread every 0.1 seconds,
    pulling code requests from the background socket thread.
    """
    if not execution_queue.empty():
        payload, result_dict, done_event = execution_queue.get()
        run_script_in_blender_main_thread(payload, result_dict, done_event)
    return 0.1 # interval in seconds for next poll

def client_handler(client_socket):
    """
    Worker thread to read client socket request, push payload to queue,
    wait for main thread execution, and reply back to the Express server.
    """
    try:
        # Read entire request payload until client closes or ends
        data = ""
        while True:
            chunk = client_socket.recv(4096).decode('utf-8')
            if not chunk:
                break
            data += chunk
            
        if not data.strip():
            client_socket.sendall("ERROR: Empty request payload".encode('utf-8'))
            return
            
        # Parse JSON request { code: "...", outputPath: "..." }
        try:
            payload = json.loads(data)
        except Exception as je:
            client_socket.sendall(f"ERROR: Invalid JSON request: {str(je)}".encode('utf-8'))
            return
            
        # Send event-wait structure to main thread timer
        done_event = threading.Event()
        result_dict = {"status": "pending", "error": None}
        
        # Enqueue request
        execution_queue.put((payload, result_dict, done_event))
        
        # Wait until Blender main thread finishes execution (timeout 15 seconds)
        success = done_event.wait(timeout=15.0)
        
        if not success:
            client_socket.sendall("ERROR: Execution Timeout (Blender took longer than 15s)".encode('utf-8'))
        elif result_dict["status"] == "success":
            client_socket.sendall("SUCCESS".encode('utf-8'))
        else:
            client_socket.sendall(f"ERROR: {result_dict['error']}".encode('utf-8'))
            
    except Exception as ex:
        print("[Aura3D Socket Thread] Exception:", str(ex))
        try:
            client_socket.sendall(f"ERROR: {str(ex)}".encode('utf-8'))
        except:
            pass
    finally:
        client_socket.close()

def socket_server_listener():
    """
    Thread listener that accepts incoming TCP connections.
    """
    global server_running
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    
    try:
        server_socket.bind(('localhost', PORT))
        server_socket.listen(5)
        print(f"[Aura3D Server Thread] TCP Listener started on localhost:{PORT}")
        
        while server_running:
            server_socket.settimeout(1.0) # Check server_running flag periodically
            try:
                client_sock, addr = server_socket.accept()
                print(f"[Aura3D Server Thread] Connection received from: {addr}")
                t = threading.Thread(target=client_handler, args=(client_sock,))
                t.daemon = True
                t.start()
            except socket.timeout:
                continue
            except Exception as accept_ex:
                if server_running:
                    print("[Aura3D Server Thread] Accept exception:", str(accept_ex))
                break
    except Exception as bind_ex:
        print(f"[Aura3D Server Thread] Could not bind to port {PORT}: {str(bind_ex)}")
    finally:
        server_socket.close()
        print("[Aura3D Server Thread] TCP Listener closed.")

# --- Start Server and Timers ---
# Remove existing timer if registered to avoid duplicate triggers on double-execution
try:
    bpy.app.timers.unregister(blender_queue_timer_poller)
except:
    pass

# Register Blender Main Thread timer poller
bpy.app.timers.register(blender_queue_timer_poller)

# Start background listener thread
server_thread = threading.Thread(target=socket_server_listener)
server_thread.daemon = True
server_thread.start()

print("="*50)
print(" Aura3D Blender Socket Server is ACTIVE!")
print(" Leave this Blender workspace open to accept tasks.")
print("="*50 + "\n")
