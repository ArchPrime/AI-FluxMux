# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 0.3.3   | ✅        |
| < 0.3.3 | ❌        |

Only the latest release is supported for security fixes.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

If you believe you have found a security vulnerability in AI-FluxMux, please report it privately:

1. **Email:** security@architectureprime.co.uk
2. **Subject:** `[AI-FluxMux] Security Vulnerability Report`
3. **Include:**
   - A description of the vulnerability
   - Steps to reproduce (if applicable)
   - Affected version(s)
   - Any relevant logs or screenshots

We aim to acknowledge all reports within **48 hours** and provide a timeline for investigation and resolution.

### What to expect

- We will confirm receipt of your report within 48 hours.
- We will investigate the report and provide a preliminary assessment.
- If the vulnerability is confirmed, we will work on a fix and coordinate a disclosure timeline with you.
- We will credit you in the release notes unless you prefer otherwise.

## Scope

AI-FluxMux is a local model router that:
- Launches and manages `llama-server` processes for local GGUF models
- Routes OpenAI-compatible requests between local and cloud models
- Stores configuration and API keys in `%AppData%\AI-FluxMux\`

### In scope

- The AI-FluxMux application and its gateway (port 5001)
- Configuration and secrets handling (`fluxmux_config.json`, `fluxmux_secrets.json`)
- Process management (llama-server launch/stop)
- Update mechanism

### Out of scope

- Third-party components (llama-server, .NET runtime, Avalonia)
- Cloud provider APIs (OpenAI, Anthropic, Gemini, DeepSeek)
- Client applications (Cline, DeepSeek Harness)

## Security Considerations

### Local Gateway

The AI-FluxMux gateway listens on `127.0.0.1:5001` with **no authentication**. This is by design for local use, but it means any local process can send requests to the gateway. If you run AI-FluxMux on a shared or multi-user system, consider:

- Restricting network access to the gateway port
- Running AI-FluxMux under a dedicated user account
- Monitoring gateway activity

### Secrets Storage

API keys and configuration are stored in plain text in `%AppData%\AI-FluxMux\`. This is a known limitation. We recommend:

- Running AI-FluxMux on a trusted, single-user system
- Protecting the `%AppData%\AI-FluxMux\` directory with file permissions
- Not sharing your `%AppData%\AI-FluxMux\` folder with others

### Process Execution

AI-FluxMux launches `llama-server` and other processes based on user configuration. A malicious configuration file could point to an arbitrary executable. We recommend:

- Only using configuration files from trusted sources
- Reviewing model paths and llama-server paths before launching

## Contact

For security questions or to report a vulnerability, contact:

- **Email:** security@architectureprime.co.uk
- **Company:** Architecture Prime Ltd.
