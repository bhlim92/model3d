using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace UniversalTestAutomator
{
    class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        static void Main(string[] args)
        {
            Console.WriteLine("=== Universal NLP UI Test Automator ===");
            
            string instruction = args.Length > 0 ? string.Join(" ", args) : "Aura3DRobotConverter를 실행하고, 가져오기 버튼을 클릭하고 캡처해줘";
            Console.WriteLine($"[입력된 자연어 명령]: {instruction}");

            // 1. NLP 파싱 (Intent Extraction)
            string targetApp = ParseAppToLaunch(instruction);
            string buttonToClick = ParseButtonToClick(instruction);
            bool requiresCapture = instruction.Contains("캡처") || instruction.Contains("스크린샷") || instruction.Contains("사진") || instruction.ToLower().Contains("capture");

            Console.WriteLine($"[분석 결과] 타겟 앱: '{targetApp}', 조작 버튼: '{buttonToClick}', 캡처 요청: {requiresCapture}");

            Process? process = null;

            // 2. 앱 구동 (Launch Intent)
            if (!string.IsNullOrEmpty(targetApp))
            {
                string exeName = targetApp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? targetApp : targetApp + ".exe";
                if (exeName.Equals("Aura3DRobotConverter.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string absPath = @"C:\Users\samsung\proj\model3d\Aura3DRobotConverter\bin\Debug\net9.0-windows\Aura3DRobotConverter.exe";
                    if (File.Exists(absPath))
                    {
                        exeName = absPath;
                    }
                }
                try
                {
                    Console.WriteLine($"[실행] '{exeName}' 구동 시도 중...");
                    process = Process.Start(new ProcessStartInfo { FileName = exeName, UseShellExecute = true });
                    Thread.Sleep(3000); // Wait for application to render
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[경고] 앱 실행 실패. 이미 실행 중일 수 있습니다: {ex.Message}");
                }
            }

            // 3. UI 자동화 기반 버튼 클릭 (Action Intent)
            if (!string.IsNullOrEmpty(buttonToClick))
            {
                Console.WriteLine($"[UI 스캔] 데스크톱 트리에서 '{buttonToClick}' 요소 탐색 시작...");
                AutomationElement? rootElement = AutomationElement.RootElement;
                Condition nameCondition = new PropertyCondition(AutomationElement.NameProperty, buttonToClick);
                
                // Polling for UI Element (Max 10 seconds)
                AutomationElement? targetElement = null;
                for (int i = 0; i < 10; i++)
                {
                    targetElement = rootElement.FindFirst(TreeScope.Descendants, nameCondition);
                    if (targetElement != null) break;
                    Thread.Sleep(1000);
                    Console.WriteLine($"[UI 스캔] 대기 중... ({i+1}/10)");
                }

                if (targetElement != null)
                {
                    try
                    {
                        InvokePattern invokePattern = (InvokePattern)targetElement.GetCurrentPattern(InvokePattern.Pattern);
                        invokePattern.Invoke();
                        Console.WriteLine($"[UI 클릭] '{buttonToClick}' 가상 클릭 전송 성공!");
                        Thread.Sleep(2000); // Wait for popup/dialog response
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[에러] '{buttonToClick}' 요소를 클릭할 수 없습니다 (Invoke 지원 안함): {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[에러] 제한시간 내에 '{buttonToClick}' 요소를 찾지 못했습니다.");
                }
            }

            // 4. 화면 캡처 (Capture Intent)
            if (requiresCapture)
            {
                Console.WriteLine("[캡처] 활성화된 타겟 창 이미지 추출 시도...");
                IntPtr hwnd = GetForegroundWindow(); // Fallback to current active window
                
                if (process != null && !process.HasExited) 
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = process.MainWindowHandle; 
                    }
                }

                if (hwnd == IntPtr.Zero || hwnd == GetForegroundWindow())
                {
                    var procs = Process.GetProcessesByName("Aura3DRobotConverter");
                    if (procs.Length > 0 && procs[0].MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = procs[0].MainWindowHandle;
                    }
                }

                if (hwnd != IntPtr.Zero)
                {
                    GetWindowRect(hwnd, out RECT rect);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w > 0 && h > 0)
                    {
                        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                        using (var gfx = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = gfx.GetHdc();
                            bool success = PrintWindow(hwnd, hdc, 2); // 2 = PW_RENDERFULLCONTENT
                            gfx.ReleaseHdc(hdc);
                            
                            if (success)
                            {
                                string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "nlp_automation_capture.png");
                                bmp.Save(savePath, ImageFormat.Png);
                                Console.WriteLine($"[성공] 캡처 증명 사진이 저장되었습니다: {savePath}");
                            }
                            else
                            {
                                Console.WriteLine("[에러] PrintWindow 캡처에 실패했습니다. (Hardware acceleration issue)");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("[에러] 캡처할 유효한 창(HWND)을 찾지 못했습니다.");
                }
            }

            // 5. 정리 
            if (process != null && !process.HasExited)
            {
                Console.WriteLine("[종료] 테스트 완료. 타겟 프로세스를 안전하게 종료합니다.");
                process.Kill();
            }
            
            Console.WriteLine("=== Universal NLP 테스트 완료 ===");
        }

        // Rule-based NLP Parsers
        static string ParseAppToLaunch(string input)
        {
            var match = Regex.Match(input, @"([a-zA-Z0-9_\-\.\:\\]+)(을|를)?\s*(실행|열기|켜)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        static string ParseButtonToClick(string input)
        {
            var match = Regex.Match(input, @"([a-zA-Z0-9가-힣\s\/_]+?)(?:버튼)?(?:을|를)?\s*(?:클릭|누르|선택)");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return string.Empty;
        }
    }
}
