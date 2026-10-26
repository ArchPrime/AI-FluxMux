# Draft: new Diagnostics topics for Help.html

Where this goes: paste under the existing **Diagnostics** topic, after the **Idle timeout** / **UI scale** paragraphs and before the **Routing decisions** sub-heading. The three items below are the 0.3.2 additions that the Diagnostics panel now carries but the help does not yet name.

Tone note: written to match the existing Diagnostics prose — plain, names the control the operator sees, says why it exists rather than just what it does.

---

## Live telemetry graph

The two graphs at the bottom of Diagnostics plot a local turn as it happens: **TTFT (s)** on top and **(tok/s)** below. TTFT is the time from the prompt to the first token — how long the model takes before it starts replying. The lower graph is throughput, tokens per second once the reply is flowing. Together they show how the loaded model is coping with the context it is currently filling, so you can see a slowdown or a stall before a real Client-app turn hits the wall.

They stay empty until a local turn runs through AI-FluxMux; **No telemetry** shows in their place until then. The **Reply speed** tick box in the Diagnostics header is what feeds the throughput side, so leave it on while you are watching a long reply and off when you are not.

## Stress test

**Stress test** runs a context-fill probe on the loaded model. It sends requests at rising fill levels — 25% up to 110% of the profile's context limit — and measures TTFT and throughput at each, so you see how the model copes as the context fills. The mode box picks how each level is driven: **Progressive** (default) builds a realistic multi-turn conversation up to the target and measures the model's real token count each turn, which is the closest to real chat; **Single prompt** sends one varied prompt per level, which is faster but less faithful.

The test is meant to run to failure. It deliberately pushes past the context limit so you can watch what actually happens — a graceful slowdown, a hang, or a crash — rather than guessing where the wall is. The result appears in the log above and is written to `context_fill_perf.log`. Use **Cancel stress test** to stop it early. Run it when the model is otherwise idle: it drives real requests, so it will compete with any chat you are running.

## Hail Mary Compactor

**Hail Mary Compactor** is a last-resort button that appears in the Diagnostics header when the current turn is close to exceeding the loaded context. It deletes the older turns from the middle of this chat's forwarded history so the current turn fits; the most recent turns and your last task are kept. Your Client app still holds the full conversation — only what is sent to the model is shortened.

The trade-off is that the model will not remember the deleted turns, so it may repeat work or lose track of an early decision. That is less disruptive than starting a new chat, where the model loses everything. If a cloud model is ready, switching to it now is the safer move — the local model keeps its full context. Use this button when you want to keep this chat on the local model.
