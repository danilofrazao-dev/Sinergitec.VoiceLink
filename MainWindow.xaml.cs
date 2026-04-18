using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json.Linq;
using WindowsInput;
using WindowsInput.Native;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Google.Apis.Auth;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace Sinergitec.VoiceLink
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        // Global Keyboard Hook Imports
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private IntPtr hookId = IntPtr.Zero;
        private LowLevelKeyboardProc proc;
        // ------------------

        bool isRecording = false;
        IWaveIn? captureDevice;
        WaveFileWriter? waveWriter;
        MemoryStream? audioStream;
        bool isLoopbackMode = false;
        string _pendingTranscription = "";
        
        IntPtr lastActiveWindowBeforeClick = IntPtr.Zero;
        DispatcherTimer focusTracker;
        IntPtr myWindowHandle = IntPtr.Zero;

        // Identity Layer (Moved to AppSecrets.cs for security)
        private static string ClientId => AppSecrets.ClientId;
        private static string ClientSecret => AppSecrets.ClientSecret;
        private GoogleJsonWebSignature.Payload? _userPayload;
        private string _userEmail = "";

        public MainWindow()
        {
            InitializeComponent();
            Log("Sinergitec Audio Bridge Initialized.");
            proc = HookCallback;
            hookId = SetHook(proc);
            this.Closed += MainWindow_Closed;
            
            this.Loaded += (s, e) => {
                myWindowHandle = new WindowInteropHelper(this).Handle;
                focusTracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                focusTracker.Tick += FocusTracker_Tick;
                focusTracker.Start();
                _ = CheckLoginOnStartup();
            };
        }

        private async Task CheckLoginOnStartup()
        {
            try
            {
                var dataStore = new FileDataStore("Sinergitec.VoiceLink.Auth");
                var token = await dataStore.GetAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>("user");
                
                // Only authorize if the user already has a cached token session, avoiding unexpected browser popups
                if (token != null)
                {
                    var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        new ClientSecrets { ClientId = ClientId, ClientSecret = ClientSecret },
                        new[] { "openid", "email", "profile" },
                        "user",
                        CancellationToken.None,
                        dataStore);

                    if (credential != null && credential.Token != null)
                    {
                        await FetchAndApplyProfile(credential);
                        LoadApiKey();
                    }
                }
            }
            catch { /* Silent fail on startup is fine */ }
        }

        private void FocusTracker_Tick(object? sender, EventArgs e)
        {
            IntPtr current = GetForegroundWindow();
            if (current != myWindowHandle && current != IntPtr.Zero) {
                lastActiveWindowBeforeClick = current;
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName!), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == 119) // F8 Virtual Key Code
                {
                    if (wParam == (IntPtr)WM_KEYDOWN)
                    {
                        if (!isRecording)
                        {
                            string hookApiKey = "";
                            Dispatcher.Invoke(() => hookApiKey = ApiKeyBox.Text.Trim());
                            if (string.IsNullOrEmpty(hookApiKey)) {
                                Dispatcher.Invoke(() => {
                                    string errorMsg = GetApiErrorMessage();
                                    MessageBox.Show(errorMsg, "Sinergitec VoiceLink", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    MainTabControl.SelectedItem = TabApi;
                                });
                            } else {
                                Dispatcher.Invoke(() => StartNormalRecording());
                            }
                        }
                    }
                    else if (wParam == (IntPtr)WM_KEYUP)
                    {
                        if (isRecording)
                            Dispatcher.Invoke(() => StopNormalRecording());
                    }
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            UnhookWindowsHookEx(hookId);
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() => {
                LogBlock.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogBlock.Text;
            });
        }

        private void UpdateStatus(string msg, string hexColor)
        {
            Dispatcher.Invoke(() => {
                StatusText.Text = msg;
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(hexColor)!;
            });
        }

        // ==========================================
        //  UI TOGGLE PUSH-TO-TALK BUTTON
        // ==========================================
        // Handling for Minimize, Maximize, Close buttons is still needed as they are inside the chrome area.


        private void ToggleMaximize()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                this.BorderThickness = new Thickness(0);
                if (MainBorder != null) MainBorder.CornerRadius = new CornerRadius(12);
                BtnMaximize.Content = "▢";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                this.BorderThickness = new Thickness(0);
                if (MainBorder != null) MainBorder.CornerRadius = new CornerRadius(0);
                BtnMaximize.Content = "❐";
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void PttButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_userEmail))
            {
                MainTabControl.SelectedItem = TabProfile;
                Log("Please Login to Google first.");
                return;
            }

            if (!isRecording) {
                bool isAutoSend = false;
                Dispatcher.Invoke(() => isAutoSend = AutoSendCheck.IsChecked == true);

                if (!isAutoSend && !string.IsNullOrEmpty(_pendingTranscription))
                {
                    Log("Pending transcription exists! Please click 'Send Now' before starting a new capture.");
                    Dispatcher.Invoke(() => BtnSendManually.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F55036")));
                    return;
                }

                string currentApiKey = "";
                Dispatcher.Invoke(() => currentApiKey = ApiKeyBox.Text.Trim());
                if (string.IsNullOrEmpty(currentApiKey)) {
                    string errorMsg = GetApiErrorMessage();
                    MessageBox.Show(errorMsg, "Sinergitec VoiceLink", MessageBoxButton.OK, MessageBoxImage.Warning);
                    MainTabControl.SelectedItem = TabApi;
                    return;
                }
                StartNormalRecording();
            } else {
                StopNormalRecording();
            }
        }

        // ==========================================
        //  EXECUTION LOGIC
        // ==========================================
        private void StartNormalRecording()
        {
            try {
                // Window Focus Safety Check
                if (lastActiveWindowBeforeClick == IntPtr.Zero || lastActiveWindowBeforeClick == myWindowHandle)
                {
                    Log("No active target window detected. Please select an application first.");
                    UpdateStatus("SELECT ACTIVE WINDOW", "#CA6F1E");
                    PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#CA6F1E")!;
                    BtnSubText.Text = "TARGET MISSING";
                    BtnSubText.Foreground = Brushes.White;
                    BtnSubText.Opacity = 1.0;
                    
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                        #pragma warning disable CA1416
                        Console.Beep(300, 200);
                        #pragma warning restore CA1416
                    }
                    return;
                }

                int sourceIndex = 0;
                Dispatcher.Invoke(() => sourceIndex = SourceCombo.SelectedIndex);
                isLoopbackMode = (sourceIndex == 1);

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    #pragma warning disable CA1416
                    Console.Beep(800, 100);
                    #pragma warning restore CA1416
                }
                isRecording = true;

                if (isLoopbackMode)
                {
                    UpdateStatus("● CAPTURING SYSTEM AUDIO", "#2DE1FF");
                    Log("Mode: System Audio Loopback (Discord/Zoom)");
                }
                else
                {
                    UpdateStatus("● RECORDING (Toggle active)", "#A93226");
                    Log("Mode: Microphone");
                }
                
                // Color update: Dimmer colors for V2
                string activeColor = isLoopbackMode ? "#CA6F1E" : "#A93226";
                PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(activeColor)!;
                PttButton.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(isLoopbackMode ? "#B05E1A" : "#7B241C")!;
                PttButton.Foreground = Brushes.White;
                BtnText.Text = GetBtnText(true);
                BtnSubText.Text = GetBtnSubText(true);
                BtnSubText.Foreground = Brushes.White;
                BtnSubText.Opacity = 1.0;

                audioStream = new MemoryStream();

                if (isLoopbackMode)
                {
                    // WASAPI Loopback: captures all system audio
                    var loopback = new WasapiLoopbackCapture();
                    var targetFormat = new WaveFormat(16000, 16, 1);

                    // Write a WAV header for 16kHz 16-bit mono
                    waveWriter = new WaveFileWriter(audioStream, targetFormat);

                    loopback.DataAvailable += (s, a) =>
                    {
                        // Loopback gives us IEEE float (typically 32-bit, stereo, 48kHz)
                        // Convert float samples to 16-bit PCM mono 16kHz
                        var sourceFormat = loopback.WaveFormat;
                        int bytesPerSample = sourceFormat.BitsPerSample / 8;
                        int channels = sourceFormat.Channels;
                        int sampleCount = a.BytesRecorded / bytesPerSample;

                        // Step 1: Convert IEEE float to float array
                        float[] floatBuffer = new float[sampleCount];
                        Buffer.BlockCopy(a.Buffer, 0, floatBuffer, 0, a.BytesRecorded);

                        // Step 2: Mix down to mono by averaging channels
                        int monoSamples = sampleCount / channels;
                        float[] mono = new float[monoSamples];
                        for (int i = 0; i < monoSamples; i++)
                        {
                            float sum = 0;
                            for (int ch = 0; ch < channels; ch++)
                                sum += floatBuffer[i * channels + ch];
                            mono[i] = sum / channels;
                        }

                        // Step 3: Resample from source rate to 16kHz
                        float ratio = 16000f / sourceFormat.SampleRate;
                        int resampledLen = (int)(monoSamples * ratio);
                        float[] resampled = new float[resampledLen];
                        for (int i = 0; i < resampledLen; i++)
                        {
                            float srcIdx = i / ratio;
                            int idx = (int)srcIdx;
                            if (idx >= monoSamples - 1) idx = monoSamples - 2;
                            float frac = srcIdx - idx;
                            resampled[i] = mono[idx] * (1 - frac) + mono[idx + 1] * frac;
                        }

                        // Step 4: Convert to 16-bit PCM and write
                        byte[] pcmBytes = new byte[resampledLen * 2];
                        for (int i = 0; i < resampledLen; i++)
                        {
                            float clamped = Math.Clamp(resampled[i], -1f, 1f);
                            short pcmVal = (short)(clamped * 32767);
                            pcmBytes[i * 2] = (byte)(pcmVal & 0xFF);
                            pcmBytes[i * 2 + 1] = (byte)((pcmVal >> 8) & 0xFF);
                        }

                        lock (audioStream)
                        {
                            waveWriter?.Write(pcmBytes, 0, pcmBytes.Length);
                        }
                    };

                    captureDevice = loopback;
                    loopback.StartRecording();
                }
                else
                {
                    // Standard microphone capture
                    var mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
                    waveWriter = new WaveFileWriter(audioStream, mic.WaveFormat);
                    mic.DataAvailable += (s, a) => audioStream.Write(a.Buffer, 0, a.BytesRecorded);
                    captureDevice = mic;
                    mic.StartRecording();
                }
            } catch (Exception ex) {
                Log($"Capture Err: {ex.Message}");
                UpdateStatus("FAIL: Capture Start", "#E74C3C");
                isRecording = false;
                BtnText.Text = GetBtnText(false);
                BtnSubText.Text = GetBtnSubText(false);
            }
        }

        private async void StopNormalRecording()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                #pragma warning disable CA1416
                Console.Beep(400, 100);
                #pragma warning restore CA1416
            }
            isRecording = false;
            UpdateStatus("Processing Audio...", "#CA6F1E");
            PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#34493D")!;
            PttButton.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#2A3B31")!;
            PttButton.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#7FD49A")!;
            BtnText.Text = GetBtnText(false);
            BtnSubText.Text = GetBtnSubText(false);
            BtnSubText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#7FD49A")!;
            BtnSubText.Opacity = 0.5;
            
            if (captureDevice != null) {
                // Must stop and dispose on UI thread for WASAPI COM safety
                captureDevice.StopRecording();
                lock (audioStream!)
                {
                    if (waveWriter != null) waveWriter.Flush();
                }
                byte[] audioBytes = audioStream != null ? audioStream.ToArray() : new byte[0];
                if (waveWriter != null) waveWriter.Dispose(); 
                captureDevice.Dispose();
                captureDevice = null;
                
                try {
                    string currentApiKey = "";
                    Dispatcher.Invoke(() => currentApiKey = ApiKeyBox.Text.Trim());
                    if (string.IsNullOrEmpty(currentApiKey)) {
                        Log("API Key is missing!");
                        UpdateStatus("Missing API Key", "#E74C3C");
                        return;
                    }

                    string keyPreview = currentApiKey.Length > 8 ? currentApiKey.Substring(0, 8) + "..." : currentApiKey;
                    Log($"Key: {keyPreview} ({currentApiKey.Length} chars)");
                    Log($"Sending {audioBytes.Length / 1024}KB to Groq...");
                    using (var client = new HttpClient()) {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", currentApiKey);
                        // Save key on first success
                        SaveApiKey(currentApiKey);
                        using (var content = new MultipartFormDataContent()) {
                            var audioContent = new ByteArrayContent(audioBytes);
                            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                            content.Add(audioContent, "file", "audio.wav");
                            content.Add(new StringContent("whisper-large-v3"), "model");
                            
                            var response = await client.PostAsync("https://api.groq.com/openai/v1/audio/transcriptions", content);
                            if (response.IsSuccessStatusCode) {
                                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                                string? text = json["text"]?.ToString().Trim();
                                if (!string.IsNullOrEmpty(text)) {
                                    Log($"\n>>> TRANSCRIPT CAPTURED:\n{text}\n");
                                    
                                    bool autoSend = false;
                                    Dispatcher.Invoke(() => {
                                        autoSend = AutoSendCheck.IsChecked == true;
                                        _pendingTranscription = text; 
                                        
                                        if (!autoSend)
                                        {
                                            BtnSendManually.Visibility = Visibility.Visible;
                                            BtnSendManually.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E5171")); // Reset highlight
                                        }
                                    });

                                    if (autoSend) {
                                        InjectIntoTarget(text);
                                        _pendingTranscription = ""; // Clear after auto-send
                                        UpdateStatus("Ready to Transcribe", "#7FD49A");
                                    } else {
                                        UpdateStatus("Transcription Ready. Awaiting Send.", "#F39C12");
                                    }
                                } else {
                                    Log("Groq returned empty text.");
                                    UpdateStatus("Ready (Nothing Heard)", "#7FD49A");
                                }
                            } else {
                                string errBody = await response.Content.ReadAsStringAsync();
                                Log($"Groq Error: {response.StatusCode} - {errBody}");
                                UpdateStatus("Groq API Error", "#E74C3C");
                            }
                        }
                    }
                } catch (Exception ex) { 
                    Log($"Net Err: {ex.Message}");
                    UpdateStatus("Network Error", "#E74C3C"); 
                }
            }
        }

        private void InjectIntoTarget(string text)
        {
            int selectedTarget = 0;
            Dispatcher.Invoke(() => selectedTarget = TargetCombo.SelectedIndex);

            if (selectedTarget == 0) // VS Code
            {
                var processes = Process.GetProcessesByName("Code").Concat(Process.GetProcessesByName("Code - Insiders")).Where(p => p.MainWindowHandle != IntPtr.Zero).ToList();
                if (processes.Count > 0) {
                    SetForegroundWindow(processes.First().MainWindowHandle);
                    Thread.Sleep(200);
                    SendPayload(text);
                } else {
                    Log("VS Code not found!");
                }
            }
            else if (selectedTarget == 1) // Universal (Active Window)
            {
                if (lastActiveWindowBeforeClick != IntPtr.Zero) {
                    SetForegroundWindow(lastActiveWindowBeforeClick);
                    Thread.Sleep(200);
                }
                SendPayload(text);
            }
            else if (selectedTarget == 2) // Windows Terminal
            {
                var processes = Process.GetProcessesByName("WindowsTerminal").Where(p => p.MainWindowHandle != IntPtr.Zero).ToList();
                if (processes.Count > 0) {
                    SetForegroundWindow(processes.First().MainWindowHandle);
                    Thread.Sleep(200);
                    SendPayload(text);
                } else {
                    Log("Windows Terminal not found!");
                }
            }
        }

        private void SendPayload(string text)
        {
            Dispatcher.Invoke(() => Clipboard.SetText(text));
            Thread.Sleep(50);
            
            var sim = new InputSimulator();
            sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
            Thread.Sleep(50);
            sim.Keyboard.KeyPress(VirtualKeyCode.RETURN);
        }

        private void BtnSendManually_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingTranscription))
            {
                InjectIntoTarget(_pendingTranscription);
                _pendingTranscription = ""; // Clear after sending
                BtnSendManually.Visibility = Visibility.Collapsed;
                BtnSendManually.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E5171"));
                Log("Payload sent manually.");
            }
        }

        private void BtnGetApiKey_Click(object sender, RoutedEventArgs e)
        {
            try {
                // Safely launch default browser using ProcessStartInfo
                Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true });
            } catch (Exception ex) {
                Log($"Failed to open browser: {ex.Message}");
            }
        }

        private string GetApiErrorMessage() {
            string lang = CmbLanguage != null && CmbLanguage.SelectedItem != null 
                          ? ((ComboBoxItem)CmbLanguage.SelectedItem).Content.ToString() 
                          : "EN";
            if (lang == "PT") return "Você deve inserir uma chave de API através da aba 'Obter API' antes de usar a gravação.";
            if (lang == "ES") return "Debe insertar una clave API a través de la pestaña 'Obtener API' antes de usar la grabación.";
            return "You must insert an API key through the 'Get API' tab before you use the capture button.";
        }

        private string GetBtnText(bool recording) {
            string lang = CmbLanguage != null && CmbLanguage.SelectedItem != null 
                          ? ((ComboBoxItem)CmbLanguage.SelectedItem).Content.ToString() 
                          : "EN";
            if (lang == "PT") return recording ? "PARAR" : "INICIAR";
            if (lang == "ES") return recording ? "DETENER" : "INICIAR";
            return recording ? "STOP" : "START";
        }

        private string GetBtnSubText(bool recording) {
            string lang = CmbLanguage != null && CmbLanguage.SelectedItem != null 
                          ? ((ComboBoxItem)CmbLanguage.SelectedItem).Content.ToString() 
                          : "EN";
            if (lang == "PT") return recording ? "GRAVANDO..." : "CAPTURA";
            if (lang == "ES") return recording ? "GRABANDO..." : "CAPTURA";
            return recording ? "RECORDING..." : "CAPTURE";
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLanguage == null || BtnText == null) return;
            string lang = ((ComboBoxItem)CmbLanguage.SelectedItem).Content?.ToString() ?? "EN";
            
            if (lang == "PT") {
                LblApiKey.Text = "CHAVE API";
                TabControls.Header = "Controles";
                TabLogs.Header = "Logs & Transcrições";
                TabApi.Header = "Obter API";
                LblSource.Text = "Fonte:";
                CmbSourceMic.Content = "🎙️ Microfone";
                CmbSourceSys.Content = "🔊 Áudio do Sistema (Discord)";
                LblTarget.Text = "Alvo:";
                CmbTargetUniversal.Content = "Universal (Janela Activa)";
                AutoSendCheck.Content = "Envio Automático";
                BtnSendManually.Content = "Enviar Agora";
                BtnText.Text = GetBtnText(isRecording);
                BtnSubText.Text = GetBtnSubText(isRecording);
                LblTests.Text = "TESTES";
                LblGetApiTitle.Text = "COMO OBTER UMA CHAVE API SEGURA";
                LblGetApiStep1.Text = "Clique no botão abaixo para abrir o Console API padrão no seu navegador.";
                LblGetApiStep2.Text = "Faça login de forma segura com Autenticação do Google e clique em 'Create API Key'.";
                LblGetApiStep3.Text = "Copie a chave, cole aqui ou na aba 'Controles' e clique para começar a gravar!";
                BtnGetApiLink.Content = "ABRIR CONSOLE API";
                LblApiKeyLink.Text = "INSERIR CHAVE API";
                if (!isRecording) UpdateStatus("Pronto para Transcrever", "#7FD49A");
            } 
            else if (lang == "ES") {
                LblApiKey.Text = "CLAVE API";
                TabControls.Header = "Controles";
                TabLogs.Header = "Logs & Transcripciones";
                TabApi.Header = "Obtener API";
                LblSource.Text = "Fuente:";
                CmbSourceMic.Content = "🎙️ Micrófono";
                CmbSourceSys.Content = "🔊 Audio del Sistema (Discord)";
                LblTarget.Text = "Objetivo:";
                CmbTargetUniversal.Content = "Universal (Ventana Activa)";
                AutoSendCheck.Content = "Envío Automático";
                BtnSendManually.Content = "Enviar Ahora";
                BtnText.Text = GetBtnText(isRecording);
                BtnSubText.Text = GetBtnSubText(isRecording);
                LblTests.Text = "PRUEBAS";
                LblGetApiTitle.Text = "CÓMO OBTENER UNA CLAVE API SEGURA";
                LblGetApiStep1.Text = "Haz clic en el botón de abajo para abrir la Consola API estándar en tu navegador.";
                LblGetApiStep2.Text = "Inicia sesión de forma segura con Google y haz clic en 'Create API Key'.";
                LblGetApiStep3.Text = "¡Copia la clave, pégala aquí o en la pestaña 'Controles' y haz clic para comenzar a grabar!";
                BtnGetApiLink.Content = "ABRIR CONSOLA API";
                LblApiKeyLink.Text = "INSERTAR CLAVE API";
                if (!isRecording) UpdateStatus("Listo para Transcribir", "#7FD49A");
            }
            else { // EN
                LblApiKey.Text = "API KEY";
                TabControls.Header = "Controls";
                TabLogs.Header = "Logs & Transcripts";
                TabApi.Header = "Get API";
                LblSource.Text = "Source:";
                CmbSourceMic.Content = "🎙️ Microphone";
                CmbSourceSys.Content = "🔊 System Audio (Discord)";
                LblTarget.Text = "Target:";
                CmbTargetUniversal.Content = "Universal (Active Window)";
                AutoSendCheck.Content = "Auto-Send Payload";
                BtnSendManually.Content = "Send Now";
                BtnText.Text = GetBtnText(isRecording);
                BtnSubText.Text = GetBtnSubText(isRecording);
                LblTests.Text = "TESTS";
                LblGetApiTitle.Text = "HOW TO GET A SECURE API KEY";
                LblGetApiStep1.Text = "Click the button below to open the standard API Console in your browser.";
                LblGetApiStep2.Text = "Sign in securely using Google Auth and click 'Create API Key'.";
                LblGetApiStep3.Text = "Copy the key, paste it here or in the 'Controls' tab, and click to start recording!";
                BtnGetApiLink.Content = "LAUNCH API CONSOLE";
                LblApiKeyLink.Text = "INSERT API KEY";
                if (!isRecording) UpdateStatus("Ready to Transcribe", "#7FD49A");
            }
        }


        // ==========================================
        //  DIAGNOSTICS & HOVER HANDLERS
        // ==========================================
        private void PttButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (isRecording) {
                string hoverRed = isLoopbackMode ? "#B05E1A" : "#7B241C"; // Darker red/orange
                PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(hoverRed)!;
            } else {
                PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#3E5C4A")!; // Lighter greenish-gray
            }
        }

        private void PttButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isRecording) {
                string baseRed = isLoopbackMode ? "#CA6F1E" : "#A93226"; // Base recording color
                PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(baseRed)!;
            } else {
                PttButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#34493D")!; // Base Idle
            }
        }

        private void BtnTestMic_Click(object sender, RoutedEventArgs e) { Log("Mic Test Running..."); }
        private void BtnTestGroq_Click(object sender, RoutedEventArgs e) { Log("Groq Test Running..."); }
        private void BtnTestInjection_Click(object sender, RoutedEventArgs e) { InjectIntoTarget("echo DIAGNOSTICS"); }

        private void LoadApiKey()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sinergitec.VoiceLink.Auth", "config.json");
                if (File.Exists(path))
                {
                    var json = JObject.Parse(File.ReadAllText(path));
                    string? savedKey = json["ApiKey"]?.ToString();
                    if (!string.IsNullOrEmpty(savedKey))
                    {
                        Dispatcher.Invoke(() => {
                            ApiKeyBox.Text = savedKey;
                            ApiKeyBoxLink.Text = savedKey;
                        });
                    }
                }
            }
            catch { }
        }

        private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ApiKeyBoxLink != null && ApiKeyBoxLink.Text != ApiKeyBox.Text)
            {
                ApiKeyBoxLink.Text = ApiKeyBox.Text;
            }
        }

        private void ApiKeyBoxLink_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ApiKeyBox != null && ApiKeyBox.Text != ApiKeyBoxLink.Text)
            {
                ApiKeyBox.Text = ApiKeyBoxLink.Text;
            }

            // High-visibility success feedback
            if (LblApiKeyFeedback != null)
            {
                string key = ApiKeyBoxLink.Text.Trim();
                if (key.StartsWith("gsk_") && key.Length >= 30)
                {
                    LblApiKeyFeedback.Visibility = Visibility.Visible;
                }
                else
                {
                    LblApiKeyFeedback.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void SaveApiKey(string key)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sinergitec.VoiceLink.Auth");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "config.json");
                var json = new JObject { ["ApiKey"] = key };
                File.WriteAllText(path, json.ToString());
            }
            catch { }
        }

        private async void BtnGoogleLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Starting Google Login...");
                BtnGoogleLogin.IsEnabled = false;

                string[] scopes = { "openid", "email", "profile" };
                
                // Branded Success Page HTML (App-Like) — using HTML entity for checkmark to avoid encoding issues
                string successHtml = @"
                <html>
                <head><meta charset='utf-8'></head>
                <body style='background: #111; color: #7FD49A; font-family: ""Segoe UI"", sans-serif; display: flex; flex-direction: column; align-items: center; justify-content: center; height: 100vh; margin: 0;'>
                    <div style='background: linear-gradient(135deg, #1A1A1A 0%, #121212 100%); width: 320px; padding: 40px; border-radius: 20px; border: 1.5px solid #7FD49A; text-align: center; box-shadow: 0 10px 40px rgba(0,0,0,0.5);'>
                        <div style='width: 70px; height: 70px; background: #252525; border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 20px; border: 1px solid #333;'>
                            <span style='font-size: 30px; font-weight: bold;'>&#10003;</span>
                        </div>
                        <h1 style='color: white; font-size: 20px; margin: 0 0 10px 0;'>Sinergitec VoiceLink</h1>
                        <p style='font-size: 14px; color: #7FD49A; margin-bottom: 30px; opacity: 0.9;'>Authentication Successful!</p>
                        <div style='font-size: 11px; color: #666; border-top: 1px solid #333; padding-top: 20px;'>
                            <p>SIGN-IN COMPLETE</p>
                            <p style='margin-top: 10px;'>You can close this tab and return to the app.</p>
                        </div>
                    </div>
                </body>
                </html>";

                var receiver = new LocalServerCodeReceiver(successHtml);
                
                var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    new ClientSecrets { ClientId = ClientId, ClientSecret = ClientSecret },
                    scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore("Sinergitec.VoiceLink.Auth"),
                    receiver);

                if (credential != null && credential.Token != null)
                {
                    await FetchAndApplyProfile(credential);
                }
            }
            catch (Exception ex)
            {
                Log($"Login Error: {ex.Message}");
                BtnGoogleLogin.IsEnabled = true;
            }
        }

        private async Task FetchAndApplyProfile(UserCredential credential)
        {
            string name = "User";
            string email = "";
            string? picture = null;

            // Try ID Token first (fast, offline validation)
            if (!string.IsNullOrEmpty(credential.Token.IdToken))
            {
                try
                {
                    var payload = await GoogleJsonWebSignature.ValidateAsync(credential.Token.IdToken);
                    _userPayload = payload;
                    name = payload.Name ?? "User";
                    email = payload.Email ?? "";
                    picture = payload.Picture;
                }
                catch { /* ID Token expired or invalid, fall through to UserInfo */ }
            }

            // Fallback: Use the Access Token to call Google's UserInfo endpoint
            if (string.IsNullOrEmpty(email))
            {
                try
                {
                    await credential.RefreshTokenAsync(CancellationToken.None);
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token.AccessToken);
                    var resp = await client.GetStringAsync("https://www.googleapis.com/oauth2/v3/userinfo");
                    var info = JObject.Parse(resp);
                    name = info["name"]?.ToString() ?? "User";
                    email = info["email"]?.ToString() ?? "";
                    picture = info["picture"]?.ToString();
                }
                catch (Exception ex)
                {
                    Log($"UserInfo fetch failed: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(email))
            {
                Log($"Connected: {name} ({email})");
                Dispatcher.Invoke(() => {
                    UpdateUiForLogin(name, email, picture);
                    SyncStatusText.Text = "\u2713 Connected";
                    SyncStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7FD49A"));
                });
            }
            else
            {
                Log("Login completed but could not retrieve profile.");
                BtnGoogleLogin.IsEnabled = true;
            }
        }

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clear stored tokens from disk
                var dataStore = new FileDataStore("Sinergitec.VoiceLink.Auth");
                await dataStore.ClearAsync();
                
                // Clear saved API key
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sinergitec.VoiceLink.Auth", "config.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* Best effort */ }

            _userPayload = null;
            _userEmail = "";
            LoggedOutPanel.Visibility = Visibility.Visible;
            LoggedInPanel.Visibility = Visibility.Collapsed;
            BtnGoogleLogin.IsEnabled = true;
            Log("Signed out. Stored credentials cleared.");
        }

        private void UpdateUiForLogin(string name, string email, string? pictureUrl)
        {
            _userEmail = email;
            UserNameText.Text = name;
            UserEmailText.Text = email;
            
            if (!string.IsNullOrEmpty(pictureUrl))
            {
                ProfileImageBrush.ImageSource = new BitmapImage(new Uri(pictureUrl));
                ProfileInitialCircle.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProfileInitial.Text = string.IsNullOrEmpty(name) ? "U" : name.Substring(0, 1).ToUpper();
                ProfileInitialCircle.Visibility = Visibility.Visible;
            }

            LoggedOutPanel.Visibility = Visibility.Collapsed;
            LoggedInPanel.Visibility = Visibility.Visible;
        }

        private void OpenSupportDonation(string amount = "")
        {
            try
            {
                // Route directly to Ko-fi profile
                string url = "https://ko-fi.com/danilofrazaodev";
                
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                Log($"Opened Ko-fi support: {(string.IsNullOrEmpty(amount) ? "Custom" : "$" + amount)}");
            }
            catch (Exception ex)
            {
                Log($"Failed to open Ko-fi: {ex.Message}");
            }
        }

        private void BtnKofiSupport_Click(object sender, RoutedEventArgs e) => OpenSupportDonation("5");
    }
}
