# Security Policy & Architecture

## 🛡️ Our Commitment to Your Privacy

Sinergitec VoiceLink was built with a **Security-First** and **Local-First** philosophy. We believe your private API keys and transcribed conversations should stay under your control.

---

## 🔒 Key Security Features

### 1. Local-Only Storage
Your API keys are stored exclusively in your local machine's `%APPDATA%` folder:
- `C:\Users\<User>\AppData\Roaming\Sinergitec.VoiceLink.Auth\api_key.txt`
No data is ever sent to Sinergitec servers. All communication happens directly between your machine and the **Transcription API**.

### 2. No Conversation Logging
VoiceLink does not store any audio or text logs of your transcriptions locally once they are sent to the target application. Every session is transient.

### 3. Open Communication (HTTPS)
All API requests are made over encrypted TLS (HTTPS) to ensure that your audio data and API keys are protected in transit.

---

## 📄 Reporting a Vulnerability

If you find a security hole in the application, please do not report it publicly. Instead:
1.  Open a private issue if possible.
2.  Contact the maintainer at **danilofrazao.dev** (or via GitHub private message).
3.  We will address it as quickly as possible.

---

**Built by danilofrazao-dev / Sinergitec Systems.**
