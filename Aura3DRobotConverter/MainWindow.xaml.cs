using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using HelixToolkit;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.SharpDX;
using Vector3 = System.Numerics.Vector3;
using Color4 = HelixToolkit.Maths.Color4;
using Aura3DRobotConverter.Models;
using Aura3DRobotConverter.Services;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Aura3DRobotConverter
{
    public class RobotTreeNode : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isExpanded = true;

        public string Name { get; set; } = string.Empty;
        public object Tag { get; set; } = null!;
        public List<RobotTreeNode> Children { get; set; } = new List<RobotTreeNode>();

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public partial class MainWindow : Window
    {
        
        private RobotConfig? _config;
        private string _sessionDir = string.Empty;
        private string _workspacePath = string.Empty;
        private RobotJoint? _selectedJoint;
        private Element3D? _currentJointHelper;
        
        // Tracking loaded visuals to redraw or modify easily
        private readonly List<Element3D> _addedRobotVisuals = new List<Element3D>();
        private readonly Dictionary<string, MeshGeometryModel3D> _linkVisualMap = new Dictionary<string, MeshGeometryModel3D>();
        private readonly Dictionary<string, HelixToolkit.Wpf.SharpDX.Material> _originalMaterials = new Dictionary<string, HelixToolkit.Wpf.SharpDX.Material>();
        private string? _highlightedLink;
        private bool _isStepFile;

        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize DirectX 11 Effects Manager
            Viewport.EffectsManager = new DefaultEffectsManager();

            // Add lights and grid programmatically
            Viewport.Items.Add(new AmbientLight3D { Color = System.Windows.Media.Color.FromArgb(255, 64, 64, 64) });
            Viewport.Items.Add(new DirectionalLight3D { Color = System.Windows.Media.Colors.White, Direction = new System.Windows.Media.Media3D.Vector3D(-1, -1, -1) });
            // Viewport.Items.Add(new AxisPlaneGridModel3D { GridSpacing = 1.0, GridThickness = 0.015, GridColor = System.Windows.Media.Colors.DarkGray });
            
            // Resolve parent directory as CAD workspace path
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _workspacePath = baseDir;
            while (!string.IsNullOrEmpty(_workspacePath))
            {
                if (Directory.Exists(Path.Combine(_workspacePath, "urdf")) && Directory.Exists(Path.Combine(_workspacePath, "Aura3DRobotConverter")))
                {
                    break;
                }
                _workspacePath = Directory.GetParent(_workspacePath)?.FullName ?? string.Empty;
            }
            if (string.IsNullOrEmpty(_workspacePath))
            {
                _workspacePath = baseDir;
            }
            
            Log($"[System] Workspace root resolved to: {_workspacePath}");
            Log("[System] Pure C# .NET STEP-to-URDF/USD Compiler Ready (No Python dependencies).");
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                LogTextBox.ScrollToEnd();
                Console.WriteLine(message);
            });
        }

        // ==========================================
        // STEP File Parsing and 3D Visual Loading
        // ==========================================

        private void OnNewFileClick(object sender, RoutedEventArgs e)
        {
            Log("[System] Clearing active workspace...");
            
            // Clear existing visuals from 3D viewport
            foreach (var visual in _addedRobotVisuals)
            {
                Viewport.Items.Remove(visual);
            }
            _addedRobotVisuals.Clear();
            _linkVisualMap.Clear();
            _originalMaterials.Clear();
            _highlightedLink = null;
            
            if (_currentJointHelper != null)
            {
                Viewport.Items.Remove(_currentJointHelper);
                _currentJointHelper = null;
            }

            // Clear configurations
            _config = null;
            _sessionDir = null;
            _isStepFile = false;
            _selectedJoint = null;

            // Reset Tree View
            RobotTreeView.Items.Clear();

            // Collapse editors
            JointEditorPanel.Visibility = Visibility.Collapsed;

            // Hide loaded file display
            if (LoadedFileBorder != null) LoadedFileBorder.Visibility = Visibility.Collapsed;
            if (LoadedFileNameTextBlock != null) LoadedFileNameTextBlock.Text = string.Empty;

            Log("[System] Workspace cleared. Ready to parse or import a new model.");
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox)
            {
                return; // Ignore if typing in textboxes
            }

            if (e.Key == System.Windows.Input.Key.T)
            {
                Log("[Viewport] Switching to Top View (Z-axis look down)...");
                if (Viewport.Camera != null)
                {
                    var cam = Viewport.Camera;
                    var pos = cam.Position;
                    var lookDir = cam.LookDirection;
                    var target = pos + lookDir;
                    
                    double distance = lookDir.Length;
                    if (distance < 0.1) distance = 5.0; // fallback

                    cam.Position = new System.Windows.Media.Media3D.Point3D(target.X, target.Y, target.Z + distance);
                    cam.LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, -distance);
                    cam.UpDirection = new System.Windows.Media.Media3D.Vector3D(1, 0, 0); // Align with preset TOP UpDirection
                }
            }
            else if (e.Key == System.Windows.Input.Key.Left && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                RotateCameraHorizontal(5.0); // Rotate 5 degrees to the left
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Right && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                RotateCameraHorizontal(-5.0); // Rotate 5 degrees to the right
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.D0 || e.Key == System.Windows.Input.Key.NumPad0)
            {
                OnIsoViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D1 || e.Key == System.Windows.Input.Key.NumPad1)
            {
                OnFrontViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D2 || e.Key == System.Windows.Input.Key.NumPad2)
            {
                OnTopViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D3 || e.Key == System.Windows.Input.Key.NumPad3)
            {
                OnRightViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D4 || e.Key == System.Windows.Input.Key.NumPad4)
            {
                OnRearViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D5 || e.Key == System.Windows.Input.Key.NumPad5)
            {
                OnBottomViewClick(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.D6 || e.Key == System.Windows.Input.Key.NumPad6)
            {
                OnLeftViewClick(this, new RoutedEventArgs());
            }
        }

        private void RotateCameraHorizontal(double angleInDegrees)
        {
            if (Viewport.Camera is HelixToolkit.Wpf.SharpDX.ProjectionCamera cam)
            {
                var pos = cam.Position;
                var lookDir = cam.LookDirection;
                var target = pos + lookDir;

                // Create a rotation matrix around Z-axis (0, 0, 1) since ModelUpDirection="0,0,1"
                var rotation = new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 0, 1), angleInDegrees);
                var matrix = System.Windows.Media.Media3D.Matrix3D.Identity;
                matrix.Rotate(rotation);

                var newLookDir = matrix.Transform(lookDir);

                // Update camera position and direction keeping the same vertical alignment
                cam.Position = target - newLookDir;
                cam.LookDirection = newLookDir;
            }
        }

        private void CameraZoom(double factor)
        {
            if (Viewport.Camera != null)
            {
                var cam = Viewport.Camera;
                var pos = cam.Position;
                var lookDir = cam.LookDirection;
                var target = pos + lookDir;

                var newLookDir = lookDir * factor;
                cam.Position = target - newLookDir;
                cam.LookDirection = newLookDir;
            }
        }

        private void SetCameraView(System.Windows.Media.Media3D.Vector3D lookDirNorm, System.Windows.Media.Media3D.Vector3D upDir)
        {
            if (Viewport.Camera != null)
            {
                var cam = Viewport.Camera;
                var pos = cam.Position;
                var lookDir = cam.LookDirection;
                var target = pos + lookDir;
                double dist = lookDir.Length;
                if (dist < 0.1) dist = 5.0;

                cam.LookDirection = lookDirNorm * dist;
                cam.Position = target - cam.LookDirection;
                cam.UpDirection = upDir;
            }
        }

        private void OnZoomInClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Zooming In...");
            CameraZoom(0.8);
        }

        private void OnZoomOutClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Zooming Out...");
            CameraZoom(1.25);
        }

        private void OnIsoViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: IsoView...");
            var isoDir = new System.Windows.Media.Media3D.Vector3D(-1, 1, -0.8);
            isoDir.Normalize();
            SetCameraView(isoDir, new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        }

        private void OnTopViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Top View (Z-axis look down)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(0, 0, -1), new System.Windows.Media.Media3D.Vector3D(1, 0, 0));
        }

        private void OnFrontViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Front View (+X to -X)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(-1, 0, 0), new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        }

        private void OnLeftViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Left View (+Y to -Y)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(0, -1, 0), new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        }

        private void OnRightViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Right View (-Y to +Y)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        }

        private void OnRearViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Rear View (-X to +X)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(1, 0, 0), new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        }

        private void OnBottomViewClick(object sender, RoutedEventArgs e)
        {
            Log("[Viewport] Setting View: Bottom View (Z-axis look up)...");
            SetCameraView(new System.Windows.Media.Media3D.Vector3D(0, 0, 1), new System.Windows.Media.Media3D.Vector3D(-1, 0, 0));
        }

        private void UpdateXmlTextBoxText()
        {
            if (_config == null) return;
            try
            {
                string xml = Services.CsharpRobotExporter.GetUrdfText(_config);
                XmlTextBox.Text = xml;
            }
            catch (Exception ex)
            {
                Log($"[Warning] Failed to generate URDF XML text: {ex.Message}");
            }
        }

        private async void OnApplyTextChangesClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(XmlTextBox.Text)) return;

            Log("[XML Editor] Applying text modifications and reloading model...");
            try
            {
                string dir = string.IsNullOrEmpty(_sessionDir) ? Path.GetTempPath() : _sessionDir;
                string tempFile = Path.Combine(dir, "temp_editor_preview.urdf");

                File.WriteAllText(tempFile, XmlTextBox.Text, System.Text.Encoding.UTF8);

                RobotConfig? parsedConfig = null;
                await Task.Run(() =>
                {
                    parsedConfig = CsharpStepParser.ParseUrdfFile(tempFile);
                });

                if (parsedConfig != null)
                {
                    _config = parsedConfig;

                    Log($"[XML Editor] Success! Parsed model name: {_config.RobotName}");
                    Log("[XML Editor] Refreshing TreeView and 3D Viewport...");
                    BuildAssemblyTreeUI();
                    await LoadRobotMeshesToViewerAsync();

                    MessageBox.Show("수정사항이 성공적으로 3D 모델에 적용되었습니다!", "성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    throw new Exception("Parsed configuration returned null.");
                }
            }
            catch (Exception ex)
            {
                Log($"[XML Editor Error] {ex.Message}");
                MessageBox.Show($"XML 파싱 중 에러가 발생했습니다:\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnOpenStepFileClick(object sender, RoutedEventArgs e)
        {
            string stepPath = ShowFileDialogSafe(
                "STEP CAD Files (*.step;*.stp)|*.step;*.stp",
                "STEP 파일 선택"
            );

            if (!string.IsNullOrEmpty(stepPath))
            {
                // Update file name display
                LoadedFileNameTextBlock.Text = Path.GetFileName(stepPath);
                LoadedFileBorder.Visibility = Visibility.Visible;

                Log($"[Parser] Loading STEP file: {stepPath}");

                // Get selected Up Axis
                string upAxis = "Z";
                if (UpAxisComboBox.SelectedItem is ComboBoxItem upItem)
                {
                    string text = upItem.Content.ToString() ?? "";
                    if (text.Contains("X")) upAxis = "X";
                    else if (text.Contains("Y")) upAxis = "Y";
                }

                // Setup local scratch conversion session directory
                string scratchRoot = Path.Combine(_workspacePath, "scratch");
                string sessionName = Path.GetFileNameWithoutExtension(stepPath) + "_conv_" + upAxis;
                _sessionDir = Path.Combine(scratchRoot, "conversions", sessionName);
                Directory.CreateDirectory(_sessionDir);

                try
                {
                    Log($"[Parser] Analyzing STEP file structure (Up Axis: {upAxis}) using native C# AnyCAD kernel...");
                    var config = await Task.Run(() => CsharpStepParser.ParseStepFile(stepPath, _sessionDir, 2700.0, upAxis));

                    if (config != null)
                    {
                        _config = config;
                        Log($"[Parser] Success! Robot Name: {_config.RobotName}. Base Link: {_config.RootLink}");
                        Log($"[Parser] Total Links: {_config.Links.Count}, Total Joints: {_config.Joints.Count}");

                        BuildAssemblyTreeUI();
                        await LoadRobotMeshesToViewerAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Error] Conversion failed: {ex.Message}");
                    MessageBox.Show($"CAD 분석 오류가 발생했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnImportModelClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(async () =>
            {
                string path = ShowFileDialogSafe(
                    "URDF Spec Files (*.urdf)|*.urdf|USD Spec Files (*.usda)|*.usda|All Files (*.*)|*.*",
                    "로봇 사양서 파일 가져오기"
                );

                if (!string.IsNullOrEmpty(path))
                {
                    string extension = Path.GetExtension(path).ToLower();
                    string directory = Path.GetDirectoryName(path) ?? string.Empty;
                    
                    Log($"[Importer] 파일 로드 시작: {path}");

                    try
                    {
                        Log("[Importer] 백그라운드 스레드에서 파일 파싱 수행 중...");
                        RobotConfig? config = await Task.Run(() =>
                        {
                            if (extension == ".urdf")
                            {
                                return CsharpStepParser.ParseUrdfFile(path);
                            }
                            else if (extension == ".usda")
                            {
                                return CsharpStepParser.ParseUsdaFile(path);
                            }
                            return null;
                        });

                        if (config != null)
                        {
                            _config = config;
                            _sessionDir = directory;

                            // Update file name display & restore UpAxis selection
                            Dispatcher.Invoke(() =>
                            {
                                LoadedFileNameTextBlock.Text = Path.GetFileName(path);
                                LoadedFileBorder.Visibility = Visibility.Visible;
                                
                                if (config.UpAxis == "Y") UpAxisComboBox.SelectedIndex = 0;
                                else UpAxisComboBox.SelectedIndex = 1;

                                try
                                {
                                    XmlTextBox.Text = File.ReadAllText(path);
                                }
                                catch (Exception ex)
                                {
                                    Log($"[Warning] Failed to read URDF/USDA file content for editor: {ex.Message}");
                                }
                            });

                            Log($"[Importer] 파싱 완료! 모델명: {_config.RobotName}. 루트 링크: {_config.RootLink}");
                            Log($"[Importer] 링크 개수: {_config.Links.Count}, 관절 개수: {_config.Joints.Count}");

                            Log("[Importer] UI 트리 구조 갱신 중...");
                            BuildAssemblyTreeUI();

                            Log("[Importer] 3D 화면에 링크 STL 메쉬 배치 및 렌더링 중...");
                            await LoadRobotMeshesToViewerAsync();
                            
                            Log("[Importer] 사양서 가져오기 완료!");
                        }
                        else
                        {
                            throw new Exception("불러온 사양서 데이터가 존재하지 않습니다.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Error] 가져오기 실패: {ex.Message}");
                        MessageBox.Show($"모델 파일을 가져오는 데 실패했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            });
        }

        private void BuildAssemblyTreeUI()
        {
            if (_config == null) return;

            RobotTreeView.Items.Clear();
            JointEditorPanel.Visibility = Visibility.Collapsed;

            // Define root tree element pointing to base link
            var rootNode = new RobotTreeNode
            {
                Name = $"{_config.RootLink} (Root)",
                Tag = _config.RootLink,
                IsExpanded = true
            };

            // Build hierarchical tree nodes recursively
            PopulateChildren(rootNode, _config.RootLink);

            RobotTreeView.Items.Add(rootNode);

            // Auto-select the first joint node to open the editor panel by default
            var firstJointNode = FindTreeNode(RobotTreeView.Items, n => n.Tag is RobotJoint);
            if (firstJointNode != null)
            {
                firstJointNode.IsSelected = true;
            }
            else
            {
                rootNode.IsSelected = true;
            }
        }

        private void PopulateChildren(RobotTreeNode parentNode, string parentLinkName, HashSet<string>? visited = null)
        {
            if (_config == null) return;

            visited ??= new HashSet<string>();
            if (visited.Contains(parentLinkName))
            {
                Log($"[Warning] 순환 참조가 감지되어 트리 재귀를 중단했습니다: {parentLinkName}");
                return;
            }
            visited.Add(parentLinkName);

            // Find all joints pointing from this parent
            var childJoints = _config.Joints.Where(j => j.Parent == parentLinkName).ToList();

            foreach (var joint in childJoints)
            {
                var jointNode = new RobotTreeNode
                {
                    Name = $"Joint: {joint.Name} ➔ {joint.Child}",
                    Tag = joint
                };

                // Add links nested under this joint
                var childLinkNode = new RobotTreeNode
                {
                    Name = joint.Child,
                    Tag = joint.Child
                };

                jointNode.Children.Add(childLinkNode);
                parentNode.Children.Add(jointNode);

                // Recurse down, copy current visited set to prevent sibling contamination
                PopulateChildren(childLinkNode, joint.Child, new HashSet<string>(visited));
            }
        }

        private System.Windows.Media.Media3D.Matrix3D GetVisualOriginTransform(RobotLink link)
        {
            var matrix = System.Windows.Media.Media3D.Matrix3D.Identity;
            if (link.VisualOriginXyz != null && link.VisualOriginXyz.Length >= 3 &&
                link.VisualOriginRpy != null && link.VisualOriginRpy.Length >= 3)
            {
                var rpy = link.VisualOriginRpy;
                // Rotate in RPY order (Roll -> Pitch -> Yaw)
                matrix.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(1, 0, 0), rpy[0] * 180.0 / Math.PI));
                matrix.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), rpy[1] * 180.0 / Math.PI));
                matrix.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 0, 1), rpy[2] * 180.0 / Math.PI));

                var xyz = link.VisualOriginXyz;
                matrix.Translate(new System.Windows.Media.Media3D.Vector3D(xyz[0], xyz[1], xyz[2]));
            }
            return matrix;
        }

        private async Task LoadRobotMeshesToViewerAsync()
        {
            if (_config == null || string.IsNullOrEmpty(_sessionDir)) return;

            Log("[Viewer] Loading and rendering 3D Link meshes in parallel...");
            
            // Clear existing visuals
            foreach (var visual in _addedRobotVisuals)
            {
                Viewport.Items.Remove(visual);
            }
            _addedRobotVisuals.Clear();
            _linkVisualMap.Clear();
            _originalMaterials.Clear();
            _highlightedLink = null;
            
            if (_currentJointHelper != null)
            {
                Viewport.Items.Remove(_currentJointHelper);
                _currentJointHelper = null;
            }

            int totalLinks = _config.Links.Count;
            var loadedMeshes = new System.Collections.Concurrent.ConcurrentBag<(RobotLink Link, HelixToolkit.SharpDX.MeshGeometry3D Geometry, bool NeedsScale)>();

            // Load all geometries in parallel on thread pool
            await Task.Run(() =>
            {
                System.Threading.Tasks.Parallel.ForEach(_config.Links, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, link =>
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                    System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
                    HelixToolkit.SharpDX.MeshGeometry3D? geometry = null;
                    bool needsScale = false;

                    if (!string.IsNullOrEmpty(link.PrimitiveType) && link.PrimitiveParams != null)
                    {
                        if (link.PrimitiveType == "box" && link.PrimitiveParams.Length >= 3)
                        {
                            geometry = BuildBox(link.PrimitiveParams[0], link.PrimitiveParams[1], link.PrimitiveParams[2]);
                        }
                        else if (link.PrimitiveType == "cylinder" && link.PrimitiveParams.Length >= 2)
                        {
                            geometry = BuildCylinder(link.PrimitiveParams[0], link.PrimitiveParams[1]);
                        }
                        else if (link.PrimitiveType == "sphere" && link.PrimitiveParams.Length >= 1)
                        {
                            geometry = BuildSphere(link.PrimitiveParams[0]);
                        }
                        needsScale = false;
                    }

                    if (geometry == null && !string.IsNullOrEmpty(link.MeshPath))
                    {
                        string resolvedPath = ResolveVisualMeshPath(link.MeshPath);
                        if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            geometry = LoadMeshWithFallback(resolvedPath);
                            needsScale = true;
                        }
                    }

                    if (geometry != null)
                    {
                        loadedMeshes.Add((link, geometry, needsScale));
                    }
                });
            });

            Log($"[Viewer] Meshes loaded. Precomputing kinematic transforms as Matrix3D...");

            // Precompute kinematic transforms in O(N) using a dictionary mapping child -> joint
            var childToJointMap = _config.Joints.ToDictionary(j => j.Child, j => j);
            var transformCache = new Dictionary<string, System.Windows.Media.Media3D.Matrix3D>();

            System.Windows.Media.Media3D.Matrix3D GetTransform(string name)
            {
                if (transformCache.TryGetValue(name, out var tf)) return tf;
                
                var matrix = System.Windows.Media.Media3D.Matrix3D.Identity;
                if (name == _config.RootLink)
                {
                    transformCache[name] = matrix;
                    return matrix;
                }

                if (childToJointMap.TryGetValue(name, out var joint))
                {
                    var local = System.Windows.Media.Media3D.Matrix3D.Identity;
                    var rpy = joint.Origin.Rpy;
                    local.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(1, 0, 0), rpy[0] * 180.0 / Math.PI));
                    local.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), rpy[1] * 180.0 / Math.PI));
                    local.Rotate(new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 0, 1), rpy[2] * 180.0 / Math.PI));
                    
                    var xyz = joint.Origin.Xyz;
                    local.Translate(new System.Windows.Media.Media3D.Vector3D(xyz[0], xyz[1], xyz[2]));

                    var parentTf = GetTransform(joint.Parent);
                    matrix = local * parentTf;
                }

                transformCache[name] = matrix;
                return matrix;
            }

            // Warm up cache for all links and log first few
            int loggedTf = 0;
            foreach (var link in _config.Links)
            {
                var m = GetTransform(link.Name);
                if (loggedTf < 5)
                {
                    Log($"[Viewer] Link {link.Name} Matrix: Offset={m.OffsetX:F4}, {m.OffsetY:F4}, {m.OffsetZ:F4}");
                    loggedTf++;
                }
            }

            Log($"[Viewer] Rendering {loadedMeshes.Count}/{totalLinks} scene nodes...");

            // Batch add visual models to the viewport on the UI thread
            int loadedCount = 0;
            Dispatcher.Invoke(() =>
            {
                foreach (var item in loadedMeshes)
                {
                    try
                    {
                        var material = HelixToolkit.Wpf.SharpDX.PhongMaterials.Gray;
                        float[]? colorRgba = item.Link.ColorRgba;
                        
                        // If no color is specified (e.g. STEP files), generate a varied but professional color based on link name hash
                        if (colorRgba == null || colorRgba.Length < 3)
                        {
                            int hash = item.Link.Name.GetHashCode();
                            float r = 0.5f + 0.3f * (float)Math.Sin(hash * 1.0);
                            float g = 0.5f + 0.3f * (float)Math.Sin(hash * 2.0);
                            float b = 0.5f + 0.3f * (float)Math.Sin(hash * 3.0);
                            colorRgba = new float[] { r, g, b, 1.0f };
                        }

                        if (colorRgba != null && colorRgba.Length >= 3)
                        {
                            float r = colorRgba[0];
                            float g = colorRgba[1];
                            float b = colorRgba[2];
                            float a = colorRgba.Length >= 4 ? colorRgba[3] : 1.0f;
                            material = new HelixToolkit.Wpf.SharpDX.PhongMaterial
                            {
                                DiffuseColor = new Color4(r, g, b, a),
                                AmbientColor = new Color4(r * 0.4f, g * 0.4f, b * 0.4f, a),
                                SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1.0f),
                                SpecularShininess = 30f
                            };
                        }

                        var model = new HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D
                        {
                            Geometry = item.Geometry,
                            Material = material,
                            Tag = item.Link.Name
                        };

                        var linkMatrix = GetTransform(item.Link.Name);
                        var visualOriginTf = GetVisualOriginTransform(item.Link);
                        if (item.NeedsScale)
                        {
                            var scaleMatrix = System.Windows.Media.Media3D.Matrix3D.Identity;
                            scaleMatrix.Scale(new System.Windows.Media.Media3D.Vector3D(0.001, 0.001, 0.001));
                            
                            if (_isStepFile)
                            {
                                var com = item.Link.CenterOfMass;
                                var localOffsetMatrix = System.Windows.Media.Media3D.Matrix3D.Identity;
                                localOffsetMatrix.Translate(new System.Windows.Media.Media3D.Vector3D(-com[0], -com[1], -com[2]));
                                
                                linkMatrix = scaleMatrix * localOffsetMatrix * linkMatrix;
                            }
                            else
                            {
                                linkMatrix = scaleMatrix * visualOriginTf * linkMatrix;
                            }
                        }
                        else
                        {
                            linkMatrix = visualOriginTf * linkMatrix;
                        }
                        
                        model.Transform = new System.Windows.Media.Media3D.MatrixTransform3D(linkMatrix);
                        
                        Viewport.Items.Add(model);
                        _addedRobotVisuals.Add(model);
                        _linkVisualMap[item.Link.Name] = model;
                        SaveOriginalMaterial(item.Link.Name, model);

                        loadedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"[Warning] Failed to render mesh {item.Link.Name}: {ex.Message}");
                    }
                }
            });

            Log($"[Viewer] Rendering completed. Total {loadedCount}/{totalLinks} meshes loaded.");
            
            // Yield control to let HelixToolkit process additions and layout
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Viewport.ZoomExtents(1000);
        }

        private string ResolveVisualMeshPath(string origMeshPath)
        {
            if (string.IsNullOrEmpty(origMeshPath)) return string.Empty;

            string normalized = origMeshPath.Replace("package://", "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string directPath = Path.GetFullPath(Path.Combine(_sessionDir, normalized));
            if (File.Exists(directPath)) return directPath;

            // Restrict recursive search to package relative paths (like URDF). Bypasses recursive search for locally converted CAD assemblies.
            if (!origMeshPath.StartsWith("package://"))
            {
                return string.Empty;
            }

            string[] parts = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;

            for (int i = 0; i < Math.Min(parts.Length, 3); i++)
            {
                string searchDir = parts[i];
                if (searchDir.Equals("robots", StringComparison.OrdinalIgnoreCase) || 
                    searchDir.Equals("meshes", StringComparison.OrdinalIgnoreCase) ||
                    searchDir.Equals("visual", StringComparison.OrdinalIgnoreCase) ||
                    searchDir.Equals("collision", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string? foundDir = FindDirectoryRecursively(_workspacePath, searchDir);
                    if (foundDir != null)
                    {
                        string remaining = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(i + 1));
                        string resolved = Path.GetFullPath(Path.Combine(foundDir, remaining));
                        if (File.Exists(resolved)) return resolved;
                    }
                }
                catch (Exception) { }
            }

            try
            {
                string filename = Path.GetFileName(normalized);
                string? foundFile = FindFileRecursively(_workspacePath, filename);
                if (foundFile != null) return foundFile;
            }
            catch (Exception) { }

            return string.Empty;
        }

        private string? FindDirectoryRecursively(string startDir, string targetDirName)
        {
            if (Path.GetFileName(startDir).Equals(targetDirName, StringComparison.OrdinalIgnoreCase))
            {
                return startDir;
            }
            foreach (var dir in Directory.GetDirectories(startDir))
            {
                string? found = FindDirectoryRecursively(dir, targetDirName);
                if (found != null) return found;
            }
            return null;
        }

        private string? FindFileRecursively(string startDir, string targetFileName)
        {
            foreach (var file in Directory.GetFiles(startDir, targetFileName))
            {
                return file;
            }
            foreach (var dir in Directory.GetDirectories(startDir))
            {
                string? found = FindFileRecursively(dir, targetFileName);
                if (found != null) return found;
            }
            return null;
        }

        private HelixToolkit.SharpDX.MeshGeometry3D? LoadMeshWithFallback(string resolvedPath)
        {
            if (string.IsNullOrEmpty(resolvedPath)) return null;

            string ext = Path.GetExtension(resolvedPath).ToLower();

            if (ext == ".dae")
            {
                string sameDirStl = Path.ChangeExtension(resolvedPath, ".stl");
                if (File.Exists(sameDirStl))
                {
                    Log($"[Viewer] DAE fallback: Found sibling STL: {Path.GetFileName(sameDirStl)}");
                    return LoadStlOrObj(sameDirStl);
                }

                string currentDirName = Path.GetFileName(Path.GetDirectoryName(resolvedPath) ?? "");
                string parentDir = Path.GetDirectoryName(Path.GetDirectoryName(resolvedPath) ?? "") ?? "";
                if (currentDirName.Equals("visual", StringComparison.OrdinalIgnoreCase))
                {
                    string collisionStl = Path.Combine(parentDir, "collision", Path.ChangeExtension(Path.GetFileName(resolvedPath), ".stl"));
                    if (File.Exists(collisionStl))
                    {
                        Log($"[Viewer] DAE fallback: Found collision STL: {Path.GetFileName(collisionStl)}");
                        return LoadStlOrObj(collisionStl);
                    }
                }
                else if (currentDirName.Equals("collision", StringComparison.OrdinalIgnoreCase))
                {
                    string visualStl = Path.Combine(parentDir, "visual", Path.ChangeExtension(Path.GetFileName(resolvedPath), ".stl"));
                    if (File.Exists(visualStl))
                    {
                        Log($"[Viewer] DAE fallback: Found visual STL: {Path.GetFileName(visualStl)}");
                        return LoadStlOrObj(visualStl);
                    }
                }

                try
                {
                    string cacheDir = Path.Combine(_workspacePath, "scratch", "mesh_cache");
                    Directory.CreateDirectory(cacheDir);
                    string cachedStl = Path.Combine(cacheDir, Path.ChangeExtension(Path.GetFileName(resolvedPath), ".stl"));
                    
                    if (File.Exists(cachedStl))
                    {
                        Log($"[Viewer] DAE fallback: Loading cached STL: {Path.GetFileName(cachedStl)}");
                        return LoadStlOrObj(cachedStl);
                    }

                    Log($"[Viewer] DAE fallback: Converting DAE to STL using AnyCAD kernel...");
                    AnyCAD.Foundation.TopoShape shape = AnyCAD.Foundation.ShapeIO.Open(resolvedPath);
                    if (shape != null)
                    {
                        bool saved = AnyCAD.Foundation.ShapeIO.Save(shape, cachedStl);
                        if (saved && File.Exists(cachedStl))
                        {
                            Log($"[Viewer] DAE fallback: Conversion successful!");
                            return LoadStlOrObj(cachedStl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Warning] AnyCAD DAE conversion failed: {ex.Message}");
                }

                Log($"[Warning] DAE file {Path.GetFileName(resolvedPath)} could not be loaded. Falling back to primitive geometry.");
                return null;
            }

            return LoadStlOrObj(resolvedPath);
        }

        private Vector3Collection CalculateNormals(Vector3Collection positions, IntCollection indices)
        {
            var normals = new Vector3Collection();
            for (int i = 0; i < positions.Count; i++)
            {
                normals.Add(new Vector3(0, 0, 0));
            }

            for (int i = 0; i < indices.Count; i += 3)
            {
                if (i + 2 >= indices.Count) break;
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];

                var v0 = positions[i0];
                var v1 = positions[i1];
                var v2 = positions[i2];

                var d1 = v1 - v0;
                var d2 = v2 - v0;
                var normal = Vector3.Cross(d1, d2);
                if (normal.LengthSquared() > 0)
                {
                    normal = Vector3.Normalize(normal);
                }

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i].LengthSquared() > 0)
                {
                    normals[i] = Vector3.Normalize(normals[i]);
                }
            }
            return normals;
        }

        private HelixToolkit.SharpDX.MeshGeometry3D? LoadStlCustom(string path)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            int index = 0;

            try
            {
                foreach (var line in System.IO.File.ReadLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                                float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                                float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                            {
                                positions.Add(new Vector3(x, y, z));
                                indices.Add(index++);
                            }
                        }
                    }
                }

                if (positions.Count == 0) return null;

                var normals = CalculateNormals(positions, indices);

                var geo = new HelixToolkit.SharpDX.MeshGeometry3D
                {
                    Positions = positions,
                    Indices = indices,
                    Normals = normals
                };
                
                return geo;
            }
            catch (Exception ex)
            {
                Log($"[Warning] Custom STL parser failed for {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private HelixToolkit.SharpDX.MeshGeometry3D? LoadStlOrObj(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLower();
                if (ext == ".stl")
                {
                    try
                    {
                        using (var fileStream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                        using (var reader = new System.IO.StreamReader(fileStream))
                        {
                            char[] buffer = new char[5];
                            int read = reader.Read(buffer, 0, 5);
                            string header = new string(buffer, 0, read);
                            if (header.Equals("solid", StringComparison.OrdinalIgnoreCase))
                            {
                                var customGeo = LoadStlCustom(path);
                                if (customGeo != null)
                                {
                                    var bounds = customGeo.Bound;
                                    Log($"[Viewer] Custom parsed ASCII STL: {Path.GetFileName(path)}, Bounds Min={bounds.Minimum}, Max={bounds.Maximum}, Vertices: {customGeo.Positions.Count}");
                                    return customGeo;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Warning] Custom ASCII STL loader check failed: {ex.Message}");
                    }

                    var readerStl = new HelixToolkit.SharpDX.StLReader();
                    var objList = readerStl.Read(path, default(HelixToolkit.SharpDX.ModelInfo));
                    if (objList != null && objList.Count > 0)
                    {
                        var geo = objList[0].Geometry as HelixToolkit.SharpDX.MeshGeometry3D;
                        if (geo != null)
                        {
                            if (geo.Normals == null || geo.Normals.Count == 0)
                            {
                                geo.Normals = CalculateNormals(geo.Positions, geo.Indices);
                            }
                            var bounds = geo.Bound;
                            Log($"[Diag] STL {Path.GetFileName(path)} bounds: Min={bounds.Minimum}, Max={bounds.Maximum}, VertexCount={geo.Positions.Count}");
                        }
                        return geo;
                    }
                }
                else if (ext == ".obj")
                {
                    var reader = new HelixToolkit.SharpDX.ObjReader();
                    var objList = reader.Read(path, default(HelixToolkit.SharpDX.ModelInfo));
                    if (objList != null && objList.Count > 0)
                    {
                        var geo = objList[0].Geometry as HelixToolkit.SharpDX.MeshGeometry3D;
                        if (geo != null)
                        {
                            if (geo.Normals == null || geo.Normals.Count == 0)
                            {
                                geo.Normals = CalculateNormals(geo.Positions, geo.Indices);
                            }
                        }
                        return geo;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Warning] Failed to load mesh file {Path.GetFileName(path)}: {ex.Message}");
            }
            return null;
        }

        private Transform3D GetLinkTransform(string linkName, HashSet<string>? visited = null)
        {
            var transformGroup = new Transform3DGroup();

            if (_config == null || linkName == _config.RootLink)
            {
                return transformGroup;
            }

            visited ??= new HashSet<string>();
            if (visited.Contains(linkName))
            {
                Log($"[Warning] 역방향 변환 트리 계산 도중 순환 참조가 감지되었습니다: {linkName}");
                return transformGroup;
            }
            visited.Add(linkName);

            var parentJoint = _config.Joints.FirstOrDefault(j => j.Child == linkName);
            if (parentJoint != null)
            {
                var localTransform = new Transform3DGroup();

                var rpy = parentJoint.Origin.Rpy;
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rpy[0] * 180.0 / Math.PI)));
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rpy[1] * 180.0 / Math.PI)));
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rpy[2] * 180.0 / Math.PI)));

                var xyz = parentJoint.Origin.Xyz;
                localTransform.Children.Add(new TranslateTransform3D(xyz[0], xyz[1], xyz[2]));

                var parentWorldTransform = GetLinkTransform(parentJoint.Parent, new HashSet<string>(visited));
                
                transformGroup.Children.Add(localTransform);
                transformGroup.Children.Add(parentWorldTransform);
            }

            return transformGroup;
        }

        private void OnRobotTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is RobotTreeNode selectedNode)
            {
                if (selectedNode.Tag is RobotJoint joint)
                {
                    _selectedJoint = joint;
                    ShowJointEditorPanel(joint);
                    HighlightJointIn3D(joint);
                    HighlightLink(joint.Child);
                    return;
                }
                else if (selectedNode.Tag is string linkName)
                {
                    HighlightLink(linkName);

                    if (_config != null)
                    {
                        var parentJoint = _config.Joints.FirstOrDefault(j => j.Child == linkName);
                        if (parentJoint != null)
                        {
                            _selectedJoint = parentJoint;
                            ShowJointEditorPanel(parentJoint);
                            HighlightJointIn3D(parentJoint);
                        }
                        else
                        {
                            _selectedJoint = null;
                            JointEditorPanel.Visibility = Visibility.Collapsed;
                            
                            if (_currentJointHelper != null)
                            {
                                Viewport.Items.Remove(_currentJointHelper);
                                _currentJointHelper = null;
                            }
                        }
                    }
                    return;
                }
            }
            _selectedJoint = null;
            JointEditorPanel.Visibility = Visibility.Collapsed;
            HighlightLink(null);
        }

        private void OnTreeViewItemSelected(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem tvi)
            {
                tvi.BringIntoView();
            }
        }

        private void OnSetOpaqueClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RobotTreeNode node)
            {
                ApplyTransparencyToNode(node, 1.0f, false);
            }
        }

        private void OnSetSemiTransparentClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RobotTreeNode node)
            {
                ApplyTransparencyToNode(node, 0.4f, false);
            }
        }

        private void OnSetTransparentClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RobotTreeNode node)
            {
                ApplyTransparencyToNode(node, 0.0f, false);
            }
        }

        private void OnSetAllOpaqueClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RobotTreeNode node)
            {
                ApplyTransparencyToNode(node, 1.0f, true);
            }
        }

        private void OnSetAllSemiTransparentClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RobotTreeNode node)
            {
                ApplyTransparencyToNode(node, 0.4f, true);
            }
        }

        private void ApplyTransparencyToNode(RobotTreeNode node, float alpha, bool recursive)
        {
            if (node.Tag is string linkName)
            {
                SetLinkTransparency(linkName, alpha);
            }
            else if (node.Tag is RobotJoint joint)
            {
                SetLinkTransparency(joint.Child, alpha);
            }
            
            if (recursive)
            {
                foreach (var child in node.Children)
                {
                    ApplyTransparencyToNode(child, alpha, true);
                }
            }
        }

        private void SetLinkTransparency(string linkName, float alpha)
        {
            // 1. Update cached original material if exists
            if (_originalMaterials.TryGetValue(linkName, out var origMat) && origMat is PhongMaterial pmOrig)
            {
                var diff = pmOrig.DiffuseColor;
                pmOrig.DiffuseColor = new Color4(diff.Red, diff.Green, diff.Blue, alpha);
                var amb = pmOrig.AmbientColor;
                pmOrig.AmbientColor = new Color4(amb.Red, amb.Green, amb.Blue, alpha);
            }

            // 2. Update model visual in the viewport
            if (_linkVisualMap.TryGetValue(linkName, out var model) && model is MeshGeometryModel3D meshModel)
            {
                if (alpha <= 0.0f)
                {
                    meshModel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    meshModel.Visibility = Visibility.Visible;
                    if (meshModel.Material is PhongMaterial pm)
                    {
                        var diff = pm.DiffuseColor;
                        pm.DiffuseColor = new Color4(diff.Red, diff.Green, diff.Blue, alpha);
                        var amb = pm.AmbientColor;
                        pm.AmbientColor = new Color4(amb.Red, amb.Green, amb.Blue, alpha);
                    }
                }
            }
        }

        private void OnViewportMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                var position = e.GetPosition(Viewport);
                var hits = Viewport.FindHits(position);
                if (hits != null && hits.Count > 0)
                {
                    foreach (var hit in hits)
                    {
                        if (hit.ModelHit is MeshGeometryModel3D model && model.Tag is string linkName)
                        {
                            SelectLinkInTreeView(linkName);
                            break;
                        }
                    }
                }
            }
        }

        private RobotTreeNode? FindTreeNode(System.Collections.IEnumerable items, Func<RobotTreeNode, bool> predicate)
        {
            foreach (var item in items)
            {
                if (item is RobotTreeNode node)
                {
                    if (predicate(node)) return node;
                    var foundChild = FindTreeNode(node.Children, predicate);
                    if (foundChild != null) return foundChild;
                }
            }
            return null;
        }

        private void SelectLinkInTreeView(string linkName)
        {
            // Clear current selection
            var currentNode = FindTreeNode(RobotTreeView.Items, n => n.IsSelected);
            if (currentNode != null)
            {
                currentNode.IsSelected = false;
            }

            // Find matching node
            var targetNode = FindTreeNode(RobotTreeView.Items, n => 
                (n.Tag is string s && s == linkName) || 
                (n.Tag is RobotJoint j && j.Child == linkName)
            );

            if (targetNode != null)
            {
                targetNode.IsSelected = true;
            }
        }

        private void ShowJointEditorPanel(RobotJoint joint)
        {
            // Temporary block event updates during parameter population
            _selectedJoint = null;

            JointEditorTitle.Text = $"관절 속성 설정: {joint.Name}";
            
            // Map Combo Box Selection
            int selectIdx = 0;
            switch (joint.Type)
            {
                case "revolute": selectIdx = 0; break;
                case "continuous": selectIdx = 1; break;
                case "prismatic": selectIdx = 2; break;
                case "fixed": selectIdx = 3; break;
            }
            JointTypeComboBox.SelectedIndex = selectIdx;

            // Axis
            AxisXTextBox.Text = joint.Axis[0].ToString();
            AxisYTextBox.Text = joint.Axis[1].ToString();
            AxisZTextBox.Text = joint.Axis[2].ToString();

            // Limits
            LimitLowerTextBox.Text = joint.Limits.Lower.ToString();
            LimitUpperTextBox.Text = joint.Limits.Upper.ToString();

            LimitsGrid.Visibility = (joint.Type == "fixed" || joint.Type == "continuous") ? Visibility.Collapsed : Visibility.Visible;

            // Re-assign pointer to reactivate updates
            _selectedJoint = joint;
            JointEditorPanel.Visibility = Visibility.Visible;
        }

        private void OnJointTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedJoint == null) return;

            string type = "revolute";
            switch (JointTypeComboBox.SelectedIndex)
            {
                case 0: type = "revolute"; break;
                case 1: type = "continuous"; break;
                case 2: type = "prismatic"; break;
                case 3: type = "fixed"; break;
            }

            _selectedJoint.Type = type;
            LimitsGrid.Visibility = (type == "fixed" || type == "continuous") ? Visibility.Collapsed : Visibility.Visible;
            Log($"[Config] Joint '{_selectedJoint.Name}' type updated to: {type}");
            UpdateXmlTextBoxText();
        }

        private void OnAxisChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedJoint == null) return;

            double.TryParse(AxisXTextBox.Text, out double x);
            double.TryParse(AxisYTextBox.Text, out double y);
            double.TryParse(AxisZTextBox.Text, out double z);

            _selectedJoint.Axis = new double[] { x, y, z };
            HighlightJointIn3D(_selectedJoint);
            UpdateXmlTextBoxText();
        }

        private void OnLimitChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedJoint == null) return;

            double.TryParse(LimitLowerTextBox.Text, out double lower);
            double.TryParse(LimitUpperTextBox.Text, out double upper);

            _selectedJoint.Limits.Lower = lower;
            _selectedJoint.Limits.Upper = upper;
            UpdateXmlTextBoxText();
        }

        private void HighlightJointIn3D(RobotJoint joint)
        {
            if (_currentJointHelper != null)
            {
                Viewport.Items.Remove(_currentJointHelper);
                _currentJointHelper = null;
            }

            // Obtain global transformation matrix of the joint base frame
            var parentTransform = GetLinkTransform(joint.Parent);
            var parentMatrix = parentTransform.Value;

            // Joint local position relative to parent in meters
            var xyz = joint.Origin.Xyz;
            var jointLocalPoint = new System.Windows.Media.Media3D.Point3D(xyz[0], xyz[1], xyz[2]);
            
            // Convert to global world space
            var jointGlobalPoint = parentMatrix.Transform(jointLocalPoint);

            // Compute global rotation axis direction
            var localAxis = new System.Windows.Media.Media3D.Vector3D(joint.Axis[0], joint.Axis[1], joint.Axis[2]);
            if (localAxis.Length < 0.1) localAxis = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
            var globalAxis = parentMatrix.Transform(localAxis);
            globalAxis.Normalize();

            // Render joint line helper
            var lineBuilder = new HelixToolkit.SharpDX.LineBuilder();
            var p1 = new Vector3((float)jointGlobalPoint.X, (float)jointGlobalPoint.Y, (float)jointGlobalPoint.Z);
            var p2 = p1 + new Vector3((float)globalAxis.X, (float)globalAxis.Y, (float)globalAxis.Z) * 0.4f;
            lineBuilder.AddLine(p1, p2);
            
            _currentJointHelper = new LineGeometryModel3D
            {
                Geometry = lineBuilder.ToLineGeometry3D(),
                Color = System.Windows.Media.Colors.Orange,
                Thickness = 5
            };

            Viewport.Items.Add(_currentJointHelper);
            
            // Adjust camera view
            if (Viewport.Camera != null)
            {
                Viewport.Camera.LookAt(jointGlobalPoint, 500);
            }
        }

        private void SaveOriginalMaterial(string linkName, MeshGeometryModel3D model)
        {
            if (_originalMaterials.ContainsKey(linkName)) return;
            _originalMaterials[linkName] = model.Material;
        }

        private void HighlightLink(string? linkName)
        {
            // Reset previous highlight
            if (!string.IsNullOrEmpty(_highlightedLink) && _linkVisualMap.TryGetValue(_highlightedLink, out var oldModel))
            {
                var origMat = _originalMaterials.TryGetValue(_highlightedLink, out var mat) ? mat : HelixToolkit.Wpf.SharpDX.PhongMaterials.Gray;
                oldModel.Material = origMat;
            }

            _highlightedLink = linkName;

            if (!string.IsNullOrEmpty(_highlightedLink) && _linkVisualMap.TryGetValue(_highlightedLink, out var newModel))
            {
                // Highlight material: vibrant Orange/Gold
                newModel.Material = new HelixToolkit.Wpf.SharpDX.PhongMaterial
                {
                    DiffuseColor = new Color4(1.0f, 0.55f, 0.0f, 1.0f)
                };
            }
        }

        private string ShowFileDialogSafe(string filter, string title)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter,
                Title = title
            };

            if (openFileDialog.ShowDialog(this) == true)
            {
                return openFileDialog.FileName;
            }
            return string.Empty;
        }

        // ==========================================
        // Robot Exporter integration
        // ==========================================

        private async void OnExportRobotClick(object sender, RoutedEventArgs e)
        {
            if (_config == null || string.IsNullOrEmpty(_sessionDir))
            {
                MessageBox.Show("먼저 STEP 파일을 불러와 주십시오.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "ZIP File (*.zip)|*.zip",
                Title = "로봇 패키지(URDF & USD) ZIP 파일 저장",
                FileName = $"{_config.RobotName}_urdf_package.zip"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                Log("[Exporter] Packaging robot assembly configuration to URDF/USD formats...");
                try
                {
                    string zipFilename = await Task.Run(() => CsharpRobotExporter.ExportRobotModel(_config, _sessionDir));
                    
                    string tempZipPath = Path.Combine(Path.GetDirectoryName(_sessionDir) ?? _sessionDir, zipFilename);
                    
                    if (File.Exists(tempZipPath))
                    {
                        if (File.Exists(saveFileDialog.FileName))
                        {
                            File.Delete(saveFileDialog.FileName);
                        }
                        File.Copy(tempZipPath, saveFileDialog.FileName);
                        Log($"[Exporter] Success! Zip package copied to: {saveFileDialog.FileName}");
                        MessageBox.Show("로봇 모델 패키지(URDF & USD) 내보내기에 성공했습니다!", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        throw new FileNotFoundException("Generated ZIP file not found inside session workspace.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Error] Export failed: {ex.Message}");
                    MessageBox.Show($"내보내기 도중 오류가 발생했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public async Task LoadModelFromFileAsync(string filePath)
        {
            Log($"[Config] loading file: {filePath}");
            string extension = Path.GetExtension(filePath).ToLower();
            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;

            try
            {
                RobotConfig? config = await Task.Run(() =>
                {
                    if (extension == ".urdf") return CsharpStepParser.ParseUrdfFile(filePath);
                    else if (extension == ".usda") return CsharpStepParser.ParseUsdaFile(filePath);
                    return null;
                });

                if (config != null)
                {
                    _config = config;
                    _sessionDir = directory;
                    _isStepFile = false;

                    // Update file name display & restore UpAxis selection
                    Dispatcher.Invoke(() =>
                    {
                        LoadedFileNameTextBlock.Text = Path.GetFileName(filePath);
                        LoadedFileBorder.Visibility = Visibility.Visible;
                        
                        if (config.UpAxis == "Y") UpAxisComboBox.SelectedIndex = 0;
                        else UpAxisComboBox.SelectedIndex = 1;

                        try
                        {
                            XmlTextBox.Text = File.ReadAllText(filePath);
                        }
                        catch (Exception ex)
                        {
                            Log($"[Warning] Failed to read URDF/USDA file content for editor: {ex.Message}");
                        }
                    });

                    BuildAssemblyTreeUI();
                    await LoadRobotMeshesToViewerAsync();
                }
            }
            catch(Exception e)
            {
                Log($"[Error] {e.Message}");
            }
        }

        public async Task LoadStepFileAsync(string filePath)
        {
            Log($"[Parser] Loading STEP file: {filePath}");
            string scratchRoot = Path.Combine(string.IsNullOrEmpty(_workspacePath) ? Path.GetDirectoryName(filePath) ?? "" : _workspacePath, "scratch");

            string upAxis = "Z";
            if (UpAxisComboBox != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (UpAxisComboBox.SelectedItem is ComboBoxItem item)
                    {
                        string text = item.Content.ToString() ?? "";
                        if (text.Contains("X")) upAxis = "X";
                        else if (text.Contains("Y")) upAxis = "Y";
                    }
                });
            }

            string sessionName = Path.GetFileNameWithoutExtension(filePath) + "_conv_" + upAxis;
            _sessionDir = Path.Combine(scratchRoot, "conversions", sessionName);
            Directory.CreateDirectory(_sessionDir);

            try {
                var config = await Task.Run(() => CsharpStepParser.ParseStepFile(filePath, _sessionDir, 2700.0, upAxis));
                if (config != null)
                {
                    _config = config;
                    _isStepFile = true;

                    // Update UI on dispatcher
                    Dispatcher.Invoke(() =>
                    {
                        LoadedFileNameTextBlock.Text = Path.GetFileName(filePath);
                        LoadedFileBorder.Visibility = Visibility.Visible;
                    });

                    BuildAssemblyTreeUI();
                    await LoadRobotMeshesToViewerAsync();
                    UpdateXmlTextBoxText();
                }
            } catch(Exception e) {
               Log($"[Error] {e.Message}");
            }
        }

        public void ExecuteQACapture(string mode)
        {
            Log("QA Capture Triggered.");
            try
            {
                // Ensure layout is updated
                Viewport.UpdateLayout();
                
                int width = (int)Viewport.ActualWidth;
                int height = (int)Viewport.ActualHeight;
                
                if (width == 0 || height == 0)
                {
                    width = 800;
                    height = 600;
                    Viewport.Measure(new System.Windows.Size(width, height));
                    Viewport.Arrange(new System.Windows.Rect(0, 0, width, height));
                    Viewport.UpdateLayout();
                }

                // Render current DX11 frame
                var rtb = Viewport.RenderBitmap();
                if (rtb == null)
                {
                    throw new Exception("RenderBitmap returned null");
                }

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

                string fileName = mode == "--qa-step" ? "qa_step_capture.png" : "qa_urdf_capture.png";
                string outPath = Path.Combine(@"C:\Users\samsung\proj\model3d\TestAutomator", fileName);
                
                using (var fs = new FileStream(outPath, FileMode.Create))
                {
                    encoder.Save(fs);
                }
                Log($"QA Capture saved to {outPath}");
                
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log($"QA Capture failed: {ex.Message}");
                System.Windows.Application.Current.Shutdown();
            }
        }

        // ==========================================
        // Dynamic Primitive Geometry Generation
        // ==========================================
        private HelixToolkit.SharpDX.MeshGeometry3D BuildBox(double x, double y, double z)
        {
            var builder = new HelixToolkit.Geometry.MeshBuilder(true, true);
            builder.AddBox(new Vector3(0, 0, 0), (float)x, (float)y, (float)z);
            return ConvertToSharpDXMesh(builder.ToMesh());
        }

        private HelixToolkit.SharpDX.MeshGeometry3D BuildCylinder(double radius, double length)
        {
            var builder = new HelixToolkit.Geometry.MeshBuilder(true, true);
            builder.AddCylinder(new Vector3(0, 0, (float)(-length / 2.0)), new Vector3(0, 0, (float)(length / 2.0)), (float)radius, 36);
            return ConvertToSharpDXMesh(builder.ToMesh());
        }

        private HelixToolkit.SharpDX.MeshGeometry3D BuildSphere(double radius)
        {
            var builder = new HelixToolkit.Geometry.MeshBuilder(true, true);
            builder.AddSphere(new Vector3(0, 0, 0), (float)radius, 24, 24);
            return ConvertToSharpDXMesh(builder.ToMesh());
        }

        private HelixToolkit.SharpDX.MeshGeometry3D ConvertToSharpDXMesh(HelixToolkit.Geometry.MeshGeometry3D geom)
        {
            var mesh = new HelixToolkit.SharpDX.MeshGeometry3D();
            if (geom.Positions != null)
            {
                mesh.Positions = new Vector3Collection(geom.Positions);
            }
            if (geom.TriangleIndices != null)
            {
                mesh.Indices = new IntCollection(geom.TriangleIndices);
            }
            if (geom.Normals != null)
            {
                mesh.Normals = new Vector3Collection(geom.Normals);
            }
            if (geom.TextureCoordinates != null)
            {
                mesh.TextureCoordinates = new Vector2Collection(geom.TextureCoordinates);
            }
            return mesh;
        }
    }
}