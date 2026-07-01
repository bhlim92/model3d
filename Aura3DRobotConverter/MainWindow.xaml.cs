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
using HelixToolkit.Wpf;
using Aura3DRobotConverter.Models;
using Aura3DRobotConverter.Services;
using System.Runtime.InteropServices;

namespace Aura3DRobotConverter
{
    public class RobotTreeNode
    {
        public string Name { get; set; } = string.Empty;
        public object Tag { get; set; } = null!;
        public List<RobotTreeNode> Children { get; set; } = new List<RobotTreeNode>();
    }

    public partial class MainWindow : Window
    {
        
        private RobotConfig? _config;
        private string _sessionDir = string.Empty;
        private string _workspacePath = string.Empty;
        private RobotJoint? _selectedJoint;
        private ArrowVisual3D? _currentJointHelper;
        
        // Tracking loaded visuals to redraw or modify easily
        private readonly List<Visual3D> _addedRobotVisuals = new List<Visual3D>();
        private readonly Dictionary<string, ModelVisual3D> _linkVisualMap = new Dictionary<string, ModelVisual3D>();
        private readonly Dictionary<string, Material> _originalMaterials = new Dictionary<string, Material>();
        private string? _highlightedLink;

        public MainWindow()
        {
            InitializeComponent();
            
            // Resolve parent directory as CAD workspace path
            _workspacePath = AppDomain.CurrentDomain.BaseDirectory;
            // Backtrack to workspace root
            for (int i = 0; i < 4; i++)
            {
                _workspacePath = Path.GetDirectoryName(_workspacePath) ?? _workspacePath;
            }
            
            Log($"[System] Workspace root resolved to: {_workspacePath}");
            Log("[System] Pure C# .NET STEP-to-URDF/USD Compiler Ready (No Python dependencies).");
            
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                string file = args[1];
                if (File.Exists(file))
                {
                    Log($"[System] CLI 파일 로드 시도: {file}");
                    await LoadModelFromFileAsync(file);
                }
            }
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        // ==========================================
        // STEP File Parsing and 3D Visual Loading
        // ==========================================

        private async void OnOpenStepFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string filter = "STEP Files (*.step;*.stp)|*.step;*.stp|All Files (*.*)|*.*";
                string selectedFile = ShowFileDialogSafe(filter, "STEP 파일 선택");

                if (!string.IsNullOrEmpty(selectedFile))
                {
                    Log($"[STEP] 변환 시작: {selectedFile}");
                    
                    try {
                        string stepContent = await File.ReadAllTextAsync(selectedFile);
                        if (stepContent.Length > 2000000) stepContent = stepContent.Substring(0, 2000000) + "\n\n... (파일 용량이 너무 커서 앞부분만 표시합니다) ...";
                        SourceCodeTextBox.Text = stepContent;
                    } catch { SourceCodeTextBox.Text = "파일을 텍스트로 읽을 수 없습니다."; }

                    // Setup local scratch conversion session directory
                    string scratchRoot = Path.Combine(_workspacePath, "scratch");
                    string sessionName = $"conv_{DateTime.Now.Ticks}_{Guid.NewGuid().ToString().Substring(0, 5)}";
                    _sessionDir = Path.Combine(scratchRoot, "conversions", sessionName);
                    Directory.CreateDirectory(_sessionDir);

                    try
                    {
                        Log("[Parser] Analyzing STEP file structure using native C# AnyCAD kernel...");
                        var config = await Task.Run(() => CsharpStepParser.ParseStepFile(selectedFile, _sessionDir));

                        if (config != null)
                        {
                            _config = config;
                            Log($"[Parser] Success! Robot Name: {_config.RobotName}. Base Link: {_config.RootLink}");
                            Log($"[Parser] Total Links: {_config.Links.Count}, Total Joints: {_config.Joints.Count}");

                            BuildAssemblyTreeUI();
                            LoadRobotMeshesToViewer();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Error] Conversion failed: {ex.Message}");
                        System.Windows.MessageBox.Show($"CAD 분석 오류가 발생했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Error] Unexpected error: {ex.Message}");
            }
        }

        private async void OnImportModelClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string filter = "Supported Robot Files|*.urdf;*.usd;*.usda|URDF Files (*.urdf)|*.urdf|USD Files (*.usd;*.usda)|*.usd;*.usda|All Files (*.*)|*.*";
                string selectedFile = ShowFileDialogSafe(filter, "로봇 사양서 파일 가져오기");

                if (!string.IsNullOrEmpty(selectedFile))
                {
                    await LoadModelFromFileAsync(selectedFile);
                }
            }
            catch (Exception ex)
            {
                Log($"[Error] Unexpected error: {ex.Message}");
            }
        }

        public async Task LoadModelFromFileAsync(string selectedFile)
        {
            Log($"[Import] 파일 로드 시도: {selectedFile}");
            
            try {
                string fileContent = await File.ReadAllTextAsync(selectedFile);
                if (fileContent.Length > 2000000) fileContent = fileContent.Substring(0, 2000000) + "\n\n... (파일 용량이 너무 커서 앞부분만 표시합니다) ...";
                SourceCodeTextBox.Text = fileContent;
            } catch { SourceCodeTextBox.Text = "파일을 텍스트로 읽을 수 없습니다."; }

            string extension = Path.GetExtension(selectedFile).ToLower();
            string directory = Path.GetDirectoryName(selectedFile) ?? string.Empty;
            
            Log($"[Importer] 파일 로드 시작: {selectedFile}");

            try
            {
                Log("[Importer] 백그라운드 스레드에서 파일 파싱 수행 중...");
                RobotConfig? config = await Task.Run(() =>
                {
                    if (extension == ".urdf") return CsharpStepParser.ParseUrdfFile(selectedFile);
                    else if (extension == ".usda") return CsharpStepParser.ParseUsdaFile(selectedFile);
                    return null;
                });

                if (config != null)
                {
                    _config = config;
                    _sessionDir = directory;

                    Log($"[Importer] 파싱 완료! 모델명: {_config.RobotName}. 루트 링크: {_config.RootLink}");
                    Log($"[Importer] 링크 개수: {_config.Links.Count}, 관절 개수: {_config.Joints.Count}");

                    Log("[Importer] UI 트리 구조 갱신 중...");
                    BuildAssemblyTreeUI();

                    Log("[Importer] 3D 화면에 링크 메쉬/도형 배치 및 렌더링 중...");
                    LoadRobotMeshesToViewer();
                    
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
                System.Windows.MessageBox.Show($"모델 파일을 가져오는 데 실패했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                Tag = _config.RootLink
            };

            // Build hierarchical tree nodes recursively
            PopulateChildren(rootNode, _config.RootLink);

            RobotTreeView.Items.Add(rootNode);
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

        private void LoadRobotMeshesToViewer()
        {
            if (_config == null || string.IsNullOrEmpty(_sessionDir)) return;

            Log("[Viewer] Loading and rendering 3D Link STL meshes...");
            
            // Clear existing visuals
            foreach (var visual in _addedRobotVisuals)
            {
                Viewport.Children.Remove(visual);
            }
            _addedRobotVisuals.Clear();
            _linkVisualMap.Clear();
            _originalMaterials.Clear();
            _highlightedLink = null;
            
            if (_currentJointHelper != null)
            {
                Viewport.Children.Remove(_currentJointHelper);
                _currentJointHelper = null;
            }

            foreach (var link in _config.Links)
            {
                Model3D? model = null;
                Transform3D? baseScaleTransform = null;
                
                if (!string.IsNullOrEmpty(link.PrimitiveType) && link.PrimitiveParams != null)
                {
                    MeshGeometry3D? primitiveMesh = null;
                    
                    if (link.PrimitiveType == "box" && link.PrimitiveParams.Length >= 3)
                    {
                        primitiveMesh = BuildBox(link.PrimitiveParams[0], link.PrimitiveParams[1], link.PrimitiveParams[2]);
                    }
                    else if (link.PrimitiveType == "cylinder" && link.PrimitiveParams.Length >= 2)
                    {
                        primitiveMesh = BuildCylinder(link.PrimitiveParams[0], link.PrimitiveParams[1]);
                    }
                    else if (link.PrimitiveType == "sphere" && link.PrimitiveParams.Length >= 1)
                    {
                        // Simplified sphere using box for now, as exact sphere math is bulky
                        double r = link.PrimitiveParams[0];
                        primitiveMesh = BuildBox(r*2, r*2, r*2);
                    }
                    
                    if (primitiveMesh != null)
                    {
                        var material = Materials.Gray;
                        if (link.ColorRgba != null && link.ColorRgba.Length >= 3)
                        {
                            byte r = (byte)(link.ColorRgba[0] * 255);
                            byte g = (byte)(link.ColorRgba[1] * 255);
                            byte b = (byte)(link.ColorRgba[2] * 255);
                            byte a = link.ColorRgba.Length >= 4 ? (byte)(link.ColorRgba[3] * 255) : (byte)255;
                            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
                            material = new DiffuseMaterial(brush);
                        }
                        
                        model = new GeometryModel3D(primitiveMesh, material);
                        baseScaleTransform = new ScaleTransform3D(1, 1, 1);
                    }
                }
                else if (!string.IsNullOrEmpty(link.MeshPath))
                {
                    string absoluteMeshPath = Path.Combine(_sessionDir, link.MeshPath);
                    if (!File.Exists(absoluteMeshPath))
                    {
                        Log($"[Warning] Mesh file missing: {absoluteMeshPath}");
                        continue;
                    }

                    string ext = Path.GetExtension(absoluteMeshPath).ToLower();
                    if (ext != ".stl" && ext != ".obj" && ext != ".dae" && ext != ".3ds")
                    {
                        Log($"[Warning] 3D 뷰어가 지원하지 않는 시각 메쉬 형식입니다: {ext} ({link.Name})");
                        continue;
                    }

                    try
                    {
                        var importer = new ModelImporter();
                        model = importer.Load(absoluteMeshPath);
                        baseScaleTransform = new ScaleTransform3D(0.001, 0.001, 0.001);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Warning] Failed to load external mesh {link.Name}: {ex.Message}");
                    }
                }

                if (model == null) continue;

                try
                {
                    var visual = new ModelVisual3D { Content = model };
                    var linkTransform = GetLinkTransform(link.Name);
                    
                    var combinedTransform = new Transform3DGroup();
                    if (baseScaleTransform != null) combinedTransform.Children.Add(baseScaleTransform);
                    combinedTransform.Children.Add(linkTransform);
                    
                    visual.Transform = combinedTransform;
                    
                    Viewport.Children.Add(visual);
                    _addedRobotVisuals.Add(visual);
                    _linkVisualMap[link.Name] = visual;
                    SaveOriginalMaterial(link.Name, visual);
                }
                catch (Exception ex)
                {
                    Log($"[Warning] Failed to apply transform to mesh {link.Name}: {ex.Message}");
                }
            }

            Viewport.ZoomExtents(1000);
        }

        private Transform3D GetLinkTransform(string linkName, HashSet<string>? visited = null)
        {
            var transformGroup = new Transform3DGroup();

            if (_config == null || linkName == _config.RootLink)
            {
                return transformGroup; // Identity transformation for root base
            }

            visited ??= new HashSet<string>();
            if (visited.Contains(linkName))
            {
                Log($"[Warning] 역방향 변환 트리 계산 도중 순환 참조가 감지되었습니다: {linkName}");
                return transformGroup;
            }
            visited.Add(linkName);

            // Backtrack parent links relative joint offset
            var parentJoint = _config.Joints.FirstOrDefault(j => j.Child == linkName);
            if (parentJoint != null)
            {
                var localTransform = new Transform3DGroup();

                // Roll (X), Pitch (Y), Yaw (Z) rotations (RPY is in radians)
                var rpy = parentJoint.Origin.Rpy;
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rpy[0] * 180.0 / Math.PI)));
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rpy[1] * 180.0 / Math.PI)));
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rpy[2] * 180.0 / Math.PI)));

                // Position offset (XYZ in meters)
                var xyz = parentJoint.Origin.Xyz;
                localTransform.Children.Add(new TranslateTransform3D(xyz[0], xyz[1], xyz[2]));

                // Multiply by parent link's accumulated world transform
                var parentWorldTransform = GetLinkTransform(parentJoint.Parent, new HashSet<string>(visited));
                
                transformGroup.Children.Add(localTransform);
                transformGroup.Children.Add(parentWorldTransform);
            }

            return transformGroup;
        }

        // ==========================================
        // UI Selection and Properties Editor Bindings
        // ==========================================

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
                    _selectedJoint = null;
                    JointEditorPanel.Visibility = Visibility.Collapsed;
                    HighlightLink(linkName);

                    // Find if there is a parent joint driving this link and draw the orange axis arrow
                    if (_config != null)
                    {
                        var parentJoint = _config.Joints.FirstOrDefault(j => j.Child == linkName);
                        if (parentJoint != null)
                        {
                            HighlightJointIn3D(parentJoint);
                        }
                        else
                        {
                            // Clear axis helper if selecting root link
                            if (_currentJointHelper != null)
                            {
                                Viewport.Children.Remove(_currentJointHelper);
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
        }

        private void OnAxisChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedJoint == null) return;

            double.TryParse(AxisXTextBox.Text, out double x);
            double.TryParse(AxisYTextBox.Text, out double y);
            double.TryParse(AxisZTextBox.Text, out double z);

            _selectedJoint.Axis = new double[] { x, y, z };
            HighlightJointIn3D(_selectedJoint);
        }

        private void OnLimitChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedJoint == null) return;

            double.TryParse(LimitLowerTextBox.Text, out double lower);
            double.TryParse(LimitUpperTextBox.Text, out double upper);

            _selectedJoint.Limits.Lower = lower;
            _selectedJoint.Limits.Upper = upper;
        }

        private void HighlightJointIn3D(RobotJoint joint)
        {
            if (_currentJointHelper != null)
            {
                Viewport.Children.Remove(_currentJointHelper);
                _currentJointHelper = null;
            }

            // Obtain global transformation matrix of the joint base frame
            var parentTransform = GetLinkTransform(joint.Parent);
            var parentMatrix = parentTransform.Value;

            // Joint local position relative to parent in meters
            var xyz = joint.Origin.Xyz;
            var jointLocalPoint = new Point3D(xyz[0], xyz[1], xyz[2]);
            
            // Convert to global world space
            var jointGlobalPoint = parentMatrix.Transform(jointLocalPoint);

            // Compute global rotation axis direction
            var localAxis = new Vector3D(joint.Axis[0], joint.Axis[1], joint.Axis[2]);
            if (localAxis.Length < 0.1) localAxis = new Vector3D(0, 0, 1);
            var globalAxis = parentMatrix.Transform(localAxis);
            globalAxis.Normalize();

            // Render arrow helper
            _currentJointHelper = new ArrowVisual3D
            {
                Point1 = jointGlobalPoint,
                Point2 = jointGlobalPoint + (globalAxis * 0.4),
                Diameter = 0.035,
                Fill = System.Windows.Media.Brushes.Orange
            };

            Viewport.Children.Add(_currentJointHelper);
            
            // Adjust camera view
            if (Viewport.Camera != null)
            {
                Viewport.Camera.LookAt(jointGlobalPoint, 500);
            }
        }

        private void SaveOriginalMaterial(string linkName, ModelVisual3D visual)
        {
            if (_originalMaterials.ContainsKey(linkName)) return;

            if (visual.Content is Model3DGroup group && group.Children.Count > 0 && group.Children[0] is GeometryModel3D geomModel)
            {
                _originalMaterials[linkName] = geomModel.Material;
            }
            else if (visual.Content is GeometryModel3D singleGeom)
            {
                _originalMaterials[linkName] = singleGeom.Material;
            }
            else
            {
                _originalMaterials[linkName] = new DiffuseMaterial(System.Windows.Media.Brushes.LightGray);
            }
        }

        private void HighlightLink(string? linkName)
        {
            // Reset previous highlight
            if (!string.IsNullOrEmpty(_highlightedLink) && _linkVisualMap.TryGetValue(_highlightedLink, out var oldVisual))
            {
                Material origMat = _originalMaterials.TryGetValue(_highlightedLink, out var mat) ? mat : new DiffuseMaterial(System.Windows.Media.Brushes.LightGray);
                SetLinkMaterial(oldVisual, origMat);
            }

            _highlightedLink = linkName;

            if (!string.IsNullOrEmpty(_highlightedLink) && _linkVisualMap.TryGetValue(_highlightedLink, out var newVisual))
            {
                // Highlight material: vibrant Orange/Gold
                var highlightMat = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0)));
                SetLinkMaterial(newVisual, highlightMat);
            }
        }

        private void SetLinkMaterial(ModelVisual3D visual, Material material)
        {
            if (visual.Content is Model3DGroup group)
            {
                SetGroupMaterial(group, material);
            }
            else if (visual.Content is GeometryModel3D geomModel)
            {
                geomModel.Material = material;
                geomModel.BackMaterial = material;
            }
        }

        private void SetGroupMaterial(Model3DGroup group, Material material)
        {
            foreach (var child in group.Children)
            {
                if (child is Model3DGroup subGroup)
                {
                    SetGroupMaterial(subGroup, material);
                }
                else if (child is GeometryModel3D geomModel)
                {
                    geomModel.Material = material;
                    geomModel.BackMaterial = material;
                }
            }
        }

        private string ShowFileDialogSafe(string filter, string title)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = filter,
                Title = title,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AutoUpgradeEnabled = false
            };

            string selectedPath = string.Empty;
            var thread = new System.Threading.Thread(() =>
            {
                if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    selectedPath = openFileDialog.FileName;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            return selectedPath;
        }

        // ==========================================
        // Robot Exporter integration
        // ==========================================

        private async void OnExportRobotClick(object sender, RoutedEventArgs e)
        {
            if (_config == null || string.IsNullOrEmpty(_sessionDir))
            {
                System.Windows.MessageBox.Show("먼저 STEP 파일을 불러와 주십시오.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
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
                        System.Windows.MessageBox.Show("로봇 모델 패키지(URDF & USD) 내보내기에 성공했습니다!", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        throw new FileNotFoundException("Generated ZIP file not found inside session workspace.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Error] Export failed: {ex.Message}");
                    System.Windows.MessageBox.Show($"내보내기 도중 오류가 발생했습니다.\n{ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        // ==========================================
        // Dynamic Primitive Geometry Generation
        // ==========================================
        private MeshGeometry3D BuildBox(double x, double y, double z)
        {
            var mesh = new MeshGeometry3D();
            double hx = x / 2, hy = y / 2, hz = z / 2;
            Point3D[] p = new Point3D[8] {
                new Point3D(-hx, -hy,  hz), new Point3D( hx, -hy,  hz),
                new Point3D( hx,  hy,  hz), new Point3D(-hx,  hy,  hz),
                new Point3D(-hx, -hy, -hz), new Point3D( hx, -hy, -hz),
                new Point3D( hx,  hy, -hz), new Point3D(-hx,  hy, -hz)
            };
            foreach(var pt in p) mesh.Positions.Add(pt);
            int[] indices = {
                0,1,2, 0,2,3, 4,7,6, 4,6,5, 0,3,7, 0,7,4,
                1,5,6, 1,6,2, 3,2,6, 3,6,7, 0,4,5, 0,5,1
            };
            foreach(int i in indices) mesh.TriangleIndices.Add(i);
            return mesh;
        }

        private MeshGeometry3D BuildCylinder(double radius, double length)
        {
            var mesh = new MeshGeometry3D();
            int segments = 16;
            double halfL = length / 2;
            for (int i = 0; i < segments; i++)
            {
                double a = 2.0 * Math.PI * i / segments;
                double x = radius * Math.Cos(a);
                double y = radius * Math.Sin(a);
                mesh.Positions.Add(new Point3D(x, y, halfL));
                mesh.Positions.Add(new Point3D(x, y, -halfL));
            }
            mesh.Positions.Add(new Point3D(0, 0, halfL));
            mesh.Positions.Add(new Point3D(0, 0, -halfL));
            
            int topC = segments * 2;
            int botC = segments * 2 + 1;
            
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int iTop = i * 2, iBot = i * 2 + 1;
                int nTop = next * 2, nBot = next * 2 + 1;
                
                mesh.TriangleIndices.Add(iTop); mesh.TriangleIndices.Add(iBot); mesh.TriangleIndices.Add(nBot);
                mesh.TriangleIndices.Add(iTop); mesh.TriangleIndices.Add(nBot); mesh.TriangleIndices.Add(nTop);
                mesh.TriangleIndices.Add(topC); mesh.TriangleIndices.Add(nTop); mesh.TriangleIndices.Add(iTop);
                mesh.TriangleIndices.Add(botC); mesh.TriangleIndices.Add(iBot); mesh.TriangleIndices.Add(nBot);
            }
            return mesh;
        }
    }
}