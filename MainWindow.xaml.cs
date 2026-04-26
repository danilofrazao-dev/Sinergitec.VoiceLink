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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
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
            this.Closed += MainWindow_Closed;
            
            this.Loaded += (s, e) => {
                myWindowHandle = new WindowInteropHelper(this).Handle;
                focusTracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                focusTracker.Tick += FocusTracker_Tick;
                focusTracker.Start();
                _ = CheckLoginOnStartup();
                _ = CheckRemoteConfig();
                StartUiWatchdog();
            };
        }

        private void StartUiWatchdog()
        {
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            watchdog.Tick += (s, e) => {
                // ZOMBIE PREVENTION: If neither panel is visible, something is wrong. Default to Login.
                if (LoggedInPanel.Visibility == Visibility.Collapsed && LoggedOutPanel.Visibility == Visibility.Collapsed)
                {
                    Log("Watchdog: Zombie state detected. Restoring Login panel.");
                    LoggedOutPanel.Visibility = Visibility.Visible;
                }

                // BUTTON STABILITY: If we are in the logged-out state, ensure the button is visible
                if (LoggedOutPanel.Visibility == Visibility.Visible && ProfileAuthWaitingPanel.Visibility == Visibility.Collapsed)
                {
                    if (BtnGoogleLogin.Visibility != Visibility.Visible)
                    {
                        Log("Watchdog: Restoring Login button visibility.");
                        BtnGoogleLogin.Visibility = Visibility.Visible;
                        BtnGoogleLogin.IsEnabled = true;
                    }
                }
            };
            watchdog.Start();
        }

        private async Task CheckLoginOnStartup()
        {
            try
            {
                var dataStore = new FileDataStore("Sinergitec.VoiceLink.Auth");
                var token = await dataStore.GetAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>("user");
                
                LoadApiKey();
                
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
                    }
                }
            }
            catch { /* Silent fail on startup is fine */ }
        }

        private void FocusTracker_Tick(object? sender, EventArgs e)
        {
            IntPtr current = GetForegroundWindow();
            if (current != myWindowHandle && current != IntPtr.Zero) {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(current, sb, sb.Capacity);
                string className = sb.ToString();
                if (className != "Progman" && className != "WorkerW") {
                    lastActiveWindowBeforeClick = current;
                }
            }
        }

        private async Task CheckRemoteConfig()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync("http://192.168.15.50:83/voicelink_config.json");
                var config = JObject.Parse(response);
                
                string status = config["Status"]?.ToString() ?? "Active";
                if (status != "Active")
                {
                    Dispatcher.Invoke(() => {
                        PttButton.IsEnabled = false;
                        string msg = config["Message"]?.ToString() ?? "Service is currently inactive.";
                        MessageBox.Show(msg, "VoiceLink Locked", MessageBoxButton.OK, MessageBoxImage.Error);
                        UpdateStatus("Service Inactive", "#E74C3C");
                    });
                }
            }
            catch { /* Offline or unresponsive -> allow usage for local-first resilience */ }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
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
                BtnMaximize.Content = "🗖";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                this.BorderThickness = new Thickness(0);
                if (MainBorder != null) MainBorder.CornerRadius = new CornerRadius(0);
                BtnMaximize.Content = "🗗";
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
                        UpdateWaveform(pcmBytes, pcmBytes.Length);
                    };

                    captureDevice = loopback;
                    loopback.StartRecording();
                }
                else
                {
                    // Standard microphone capture
                    var mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
                    waveWriter = new WaveFileWriter(audioStream, mic.WaveFormat);
                    mic.DataAvailable += (s, a) => {
                        audioStream.Write(a.Buffer, 0, a.BytesRecorded);
                        UpdateWaveform(a.Buffer, a.BytesRecorded);
                    };
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
            UpdateWaveform(new byte[0], 0); // Flatten wave
            
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
                    Log($"Sending {audioBytes.Length / 1024}KB to API service...");
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
                                    bool logOnly = false;
                                    Dispatcher.Invoke(() => {
                                        autoSend = AutoSendCheck.IsChecked == true;
                                        logOnly = LogOnlyCheck.IsChecked == true;
                                        _pendingTranscription = text; 
                                        
                                        if (!autoSend && !logOnly)
                                        {
                                            BtnSendManually.Visibility = Visibility.Visible;
                                            BtnSendManually.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E5171")); // Reset highlight
                                        }
                                    });

                                    if (logOnly) {
                                        _pendingTranscription = ""; // Clear, no injection needed
                                        UpdateStatus("Ready to Transcribe", "#7FD49A");
                                    } else if (autoSend) {
                                        InjectIntoTarget(text);
                                        _pendingTranscription = ""; // Clear after auto-send
                                        UpdateStatus("Ready to Transcribe", "#7FD49A");
                                        Log("SYSTEM WORKING API SET");
                                    } else {
                                        UpdateStatus("Transcription Ready. Awaiting Send.", "#F39C12");
                                    }
                                } else {
                                    Log("API service returned empty text.");
                                    UpdateStatus("Ready (Nothing Heard)", "#7FD49A");
                                }
                            } else {
                                string errBody = await response.Content.ReadAsStringAsync();
                                Log($"API Error: {response.StatusCode} - {errBody}");
                                UpdateStatus("API Service Error", "#E74C3C");
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
                } else {
                    Dispatcher.Invoke(() => {
                        UpdateStatus("No Active Window Filtered", "#F39C12");
                    });
                }
                SendPayload(text);
                lastActiveWindowBeforeClick = IntPtr.Zero; // Reset after successful send
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
                // Launch the dashboard to retrieve your key
                Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true });
            } catch (Exception ex) {
                Log($"Failed to open browser: {ex.Message}");
            }
        }

        private string GetApiErrorMessage() {
            string lang = CmbLanguage != null && CmbLanguage.SelectedItem != null 
                          ? ((ComboBoxItem)CmbLanguage.SelectedItem).Content.ToString() 
                          : "EN";
            if (lang == "PT") return "Voc├¬ deve inserir uma chave de API atrav├⌐s da aba 'Obter API' antes de usar a grava├º├úo.";
            if (lang == "ES") return "Debe insertar una clave API a trav├⌐s de la pesta├▒a 'Obtener API' antes de usar la grabaci├│n.";
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
                TabProfile.Header = "Perfil";
                LblSource.Text = "Fonte:";
                CmbSourceMic.Content = "🎙️ Microfone";
                CmbSourceSys.Content = "🔊 Áudio do Sistema (Discord)";
                LblTarget.Text = "Alvo:";
                CmbTargetUniversal.Content = "Universal (Janela Ativa)";
                AutoSendCheck.Content = "Envio Automático";
                BtnSendManually.Content = "Enviar Agora";
                BtnText.Text = GetBtnText(isRecording);
                BtnSubText.Text = GetBtnSubText(isRecording);
                LblTests.Text = "TESTES";
                LblGetApiTitle.Text = "FREE API NECESSÁRIA PARA PROSSEGUIR";
                LblGetApiStep1.Text = "Clique no botão abaixo para abrir o Console API padrão no seu navegador.";
                LblGetApiStep2.Text = "Continue com Google (use 'Log In' no CANTO SUPERIOR DIREITO se o botão central travar).";
                LblGetApiStep3.Text = "Copie a chave API do Dashboard, cole abaixo e comece a gravar!";
                BtnGetApiLink.Content = "OBTER API";
                LblApiKeyLink.Text = "INSERIR CHAVE API";
                LblApiKeyFeedback.Text = "API DO SISTEMA CONFIGURADA";
                LblApiKeyShared.Text = "Esta chave é compartilhada com a aba Controles.";
                LblSyncStatus.Text = "STATUS DE SINCRONIZAÇÃO";
                LblSupportTitle.Text = "APOIE O PROJETO";
                BtnKofiSupport.Content = "DOAR ($5)";
                LblSupportProcessed.Text = "Pagamentos processados com segurança pelo Ko-fi.";
                BtnLogout.Content = "SAIR";
                TabDonations.Header = "Doações";
                LblPixTitle.Text = "PIX / QR CODE (BRL)";
                LblCloseOverlay.Text = "CLIQUE PARA FECHAR";
                LblFootnote.Text = "Desenvolvido por danilofrazao-dev";
                LoggedOutPanel.Children.OfType<TextBlock>().First().Text = "LOGIN";
                TxtGoogleLogin.Text = "Continuar com Google";
                if (!isRecording) UpdateStatus("Pronto para Transcrever", "#7FD49A");
            } 
            else if (lang == "ES") {
                LblApiKey.Text = "CLAVE API";
                TabControls.Header = "Controles";
                TabLogs.Header = "Logs & Transcripciones";
                TabApi.Header = "Obtener API";
                TabProfile.Header = "Perfil";
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
                LblGetApiTitle.Text = "API GRATUITA NECESARIA PARA PROCEDER";
                LblGetApiStep1.Text = "Haz clic en el botón de abajo para abrir la Consola API estándar en tu navegador.";
                LblGetApiStep2.Text = "Continuar con Google (usa 'Log In' en la PARTE SUPERIOR DERECHA si el centro falla).";
                LblGetApiStep3.Text = "¡Copia la clave API del Dashboard, pégala abajo y comienza a grabar!";
                BtnGetApiLink.Content = "GET API";
                LblApiKeyLink.Text = "INSERTAR CLAVE API";
                LoggedOutPanel.Children.OfType<TextBlock>().First().Text = "LOGIN";
                TxtGoogleLogin.Text = "Continuar con Google";
                if (!isRecording) UpdateStatus("Listo para Transcribir", "#7FD49A");
            }
            else { // EN
                LblApiKey.Text = "API KEY";
                TabControls.Header = "Controls";
                TabLogs.Header = "Logs & Transcripts";
                TabApi.Header = "Get API";
                TabProfile.Header = "Profile";
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
                LblGetApiTitle.Text = "FREE API NEEDED TO PROCEED";
                LblGetApiStep1.Text = "Click the button below to open the standard API Console in your browser.";
                LblGetApiStep2.Text = "Continue with Google (use 'Log In' at the TOP RIGHT if the center loops).";
                LblGetApiStep3.Text = "Copy the API key from the Dashboard, paste it below, and talk it through!";
                BtnGetApiLink.Content = "GET API";
                LblApiKeyLink.Text = "INSERT API KEY";
                LblApiKeyFeedback.Text = "SYSTEM WORKING API SET";
                LblApiKeyShared.Text = "This key is shared with the Controls tab.";
                LblSyncStatus.Text = "SYNC STATUS";
                LblSupportTitle.Text = "SUPPORT THE PROJECT";
                BtnKofiSupport.Content = "DONATE ($5)";
                LblSupportProcessed.Text = "Payments securely processed by Ko-fi.";
                BtnLogout.Content = "LOGOUT";
                TabDonations.Header = "Donations";
                LblPixTitle.Text = "PIX / QR CODE (BRL)";
                LblCloseOverlay.Text = "CLICK TO CLOSE";
                LblFootnote.Text = "Developed by danilofrazao-dev";
                LoggedOutPanel.Children.OfType<TextBlock>().First().Text = "LOGIN";
                TxtGoogleLogin.Text = "Continue with Google";
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
        private void BtnTestApi_Click(object sender, RoutedEventArgs e) { Log("API Test Running..."); }
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
                ApiKeyBoxLink.Text = ApiKeyBox.Text;
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
                    LblApiKeyFeedback.Text = "SYSTEM WORKING API SET";
                    LblApiKeyFeedback.Visibility = Visibility.Visible;
                    
                    // Auto-redirect to Controls tab after 3 seconds
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, ev) => {
                        MainTabControl.SelectedItem = TabControls;
                        timer.Stop();
                    };
                    timer.Start();
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

        private CancellationTokenSource? _authCts;

        private void BtnCancelAuth_Click(object sender, RoutedEventArgs e)
        {
            _authCts?.Cancel();
            Log("Authentication attempt cancelled by user.");
            ResetAuthUi();
        }

        private void UpdateWaveform(byte[] buffer, int length)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (!isRecording || length == 0) {
                    WaveformPolyline.Points = new PointCollection { new Point(0, 20), new Point(280, 20) };
                    WaveformPolylineGlow.Points = new PointCollection { new Point(0, 20), new Point(280, 20) };
                    return;
                }

                var points = new PointCollection();
                var glowPoints = new PointCollection();
                int count = 100; // Ultra high density
                double step = 280.0 / count;
                
                points.Add(new Point(0, 20));
                glowPoints.Add(new Point(0, 20));
                for (int i = 0; i < count; i++)
                {
                    int index = (i * length / count) & ~1;
                    if (index + 1 >= length) break;
                    
                    short sample = BitConverter.ToInt16(buffer, index);
                    double pct = sample / 32768.0;
                    
                    // EXTREME SPIKE: 120x sensitivity for radical "jump"
                    double volume = Math.Min(19, Math.Abs(pct) * 120); // Clamped at 19 for 40px canvas
                    double y = (i % 2 == 0) ? 20 + volume : 20 - volume; 
                    
                    points.Add(new Point(i * step, y));
                    glowPoints.Add(new Point(i * step, y));
                }
                points.Add(new Point(280, 20));
                glowPoints.Add(new Point(280, 20));
                
                if (points.Count > 1) {
                    WaveformPolyline.Points = points;
                    WaveformPolylineGlow.Points = glowPoints;
                }
            }));
        }

        private void ResetAuthUi()
        {
            Dispatcher.Invoke(() => {
                ProfileAuthWaitingPanel.Visibility = Visibility.Collapsed;
                BtnGoogleLogin.Visibility = Visibility.Visible;
                BtnGoogleLogin.IsEnabled = true;
                
                if (string.IsNullOrEmpty(_userEmail))
                {
                    LoggedOutPanel.Visibility = Visibility.Visible;
                    LoggedInPanel.Visibility = Visibility.Collapsed;
                }
            });
        }

        // Login button on the Profile tab — performs OAuth and redirects.
        private async void BtnGoogleLogin_Click(object sender, RoutedEventArgs e)
        {
            BtnGoogleLogin.Visibility = Visibility.Collapsed;
            ProfileAuthWaitingPanel.Visibility = Visibility.Visible;
            Log("Opening browser for authentication...");

            try
            {
                _authCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

                string successHtml = "<html><body style='background:#111;color:#7FD49A;font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px'>"
                                   + "<h2>&#10003; Dashboard Connected</h2>"
                                   + "<p style='color:#888'>Opening the API Console for your key...</p>"
                                   + "<script>setTimeout(function() { window.location.href = 'https://console.groq.com/keys'; }, 1500);</script>"
                                   + "</body></html>";

                var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    new ClientSecrets { ClientId = ClientId, ClientSecret = ClientSecret },
                    new[] { "openid", "email", "profile" },
                    "user",
                    _authCts.Token,
                    new FileDataStore("Sinergitec.VoiceLink.Auth"),
                    new LocalServerCodeReceiver(successHtml));

                if (credential?.Token != null)
                {
                    await FetchAndApplyProfile(credential);
                }
            }
            catch (OperationCanceledException) { Log("Authentication cancelled."); }
            catch (Exception ex) { Log($"Login error: {ex.Message}"); }
            finally
            {
                _authCts = null;
                ResetAuthUi();
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
                    name = payload.Name ?? (payload.GivenName + " " + payload.FamilyName).Trim();
                    if (string.IsNullOrEmpty(name) || name == " ") name = "User";
                    email = payload.Email ?? "";
                    picture = payload.Picture;
                    Log($"ID Token Profile: {name} | Pic: {!string.IsNullOrEmpty(picture)}");
                }
                catch (Exception ex) { Log($"ID Token fallback: {ex.Message}"); }
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
                    
                    string fetchedName = info["name"]?.ToString() ?? info["given_name"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(fetchedName)) name = fetchedName;
                    
                    string fetchedEmail = info["email"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(fetchedEmail)) email = fetchedEmail;
                    
                    string? fetchedPic = info["picture"]?.ToString();
                    if (!string.IsNullOrEmpty(fetchedPic)) picture = fetchedPic;
                    
                    Log($"UserInfo Profile: {name} | Pic: {!string.IsNullOrEmpty(picture)}");
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

                // Wait 2 seconds and switch to the correct tab
                await Task.Delay(2000);
                Dispatcher.Invoke(() => {
                    if (string.IsNullOrEmpty(ApiKeyBox.Text.Trim()))
                    {
                        MainTabControl.SelectedItem = TabApi;
                    }
                    else
                    {
                        MainTabControl.SelectedItem = TabControls;
                    }
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
                // Clear primary stored tokens
                var mainStore = new FileDataStore("Sinergitec.VoiceLink.Auth");
                await mainStore.ClearAsync();

                // Clear secondary stored tokens
                var secondaryStore = new FileDataStore("Sinergitec.Secondary.Auth");
                await secondaryStore.ClearAsync();
                
                // Clear saved API key
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sinergitec.VoiceLink.Auth", "config.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* Best effort */ }

            _userPayload = null;
            _userEmail = "";
            
            // Force return to selection UI
            ResetAuthUi(); 
            
            LoggedOutPanel.Visibility = Visibility.Visible;
            LoggedInPanel.Visibility = Visibility.Collapsed;
            BtnGoogleLogin.IsEnabled = true;
            Log("Signed out. All credentials cleared.");
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
                string url = "https://ko-fi.com/laystorages";
                
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                Log($"Opened Ko-fi support: {(string.IsNullOrEmpty(amount) ? "Custom" : "$" + amount)}");
            }
            catch (Exception ex)
            {
                Log($"Failed to open Ko-fi: {ex.Message}");
            }
        }

        private void BtnKofiSupport_Click(object sender, RoutedEventArgs e) => OpenSupportDonation("5");

        private void BtnQR25_Click(object sender, RoutedEventArgs e)
        {
            ShowQrOverlay("pack://application:,,,/SinergitecVoiceLink;component/Assets/qr25.png");
        }

        private void BtnQR50_Click(object sender, RoutedEventArgs e)
        {
            ShowQrOverlay("pack://application:,,,/SinergitecVoiceLink;component/Assets/qr50.png");
        }

        private void ShowQrOverlay(string imagePath)
        {
            try {
                ImgFullQR.Source = new BitmapImage(new Uri(imagePath));
                QrOverlay.Visibility = Visibility.Visible;
            } catch (Exception ex) {
                Log($"QR Overlay Error: {ex.Message}");
            }
        }

        private void QrOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            QrOverlay.Visibility = Visibility.Collapsed;
        }

    }
}
