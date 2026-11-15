# SPEC — 159 · Ditado por voz (STT) no composer

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `composer-voice-stt` |
| Type | `Frontend` |
| Stack | `.NET 10 · Blazor WASM · Web Speech API` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #457 · Epic #451 |
| Scope | `src/Sandcastle.Client/` + `tests/` |

## 1. User Story

**As a** usuário, **I want** ditar a mensagem ao agente pelo microfone
**so that** escrevo instruções longas sem digitar — mesma entrada do
`VoiceButton` do OpenWebUiCode (STT no composer, com estados e erros de
mic explícitos).

## 2. Contexto / escopo

Somente **entrada** de voz nesta SPEC: botão de microfone no
`ChatComposer` que transcreve para o input via Web Speech API
(`SpeechRecognition`) com fallback honesto (botão oculto/tooltip
"indisponível neste navegador" quando a API não existe). Estados:
idle → listening → transcribing → erro (mic negado/sem suporte) com
mensagens i18n. Transcrição vai para o texto do composer (editável,
não envia sozinho; opção "enviar ao parar" off por default).

Fora de escopo: TTS/leitura das respostas, modo Call, STT server-side
(Whisper), comandos de voz (ações fora do texto).

## 3. Requisitos

- RF-001: `VoiceButton` component no `ChatComposer` (à esquerda do
  botão enviar) usando `SpeechRecognition` via JS interop
  (`wwwroot/js/speech.js`); `lang` segue o locale do usuário (mapeado
  das 8 línguas); `continuous=true`, `interimResults=true` com texto
  parcial renderizado no input.
- RF-002: estados visíveis — idle (mic), listening (pulso vermelho),
  erro (`mic_denied`/`not_supported`/`no_speech`) com toast/tooltip;
  click em listening para para.
- RF-003: resultado final concatena ao texto existente (não substitui);
  não envia automaticamente.
- RF-004: i18n ×8 para todos os estados/erros; ARIA label + status
  `aria-live` para o texto parcial.
- RF-005: testes bUnit do estado do componente com JS interop mockado;
  guard manual documentado (mic real não testável em CI).

## 4. API Contract

Sem backend. JS interop: `speechInterop.start(lang)`, `.stop()`,
callback `onresult(text,isFinal)`/`onerror(code)`.

## 5. Critérios de aceite

- **Dado** Chrome com mic, **quando** clico e falo, **então** o texto
  aparece no composer e posso editar antes de enviar.
- **Dado** Firefox sem `SpeechRecognition`, **então** botão renderiza
  desabilitado/oculto com hint, sem erro JS.
- **Dado** permissão negada, **então** erro `mic_denied` traduzido e o
  input não é alterado.

## 6. Task Plan

1. `wwwroot/js/speech.js` wrapper + feature-detect.
2. `VoiceButton.razor` + wiring no `ChatComposer`.
3. i18n ×8 + aria + testes bUnit (interop mock) + drift guards.

## 7. Deliverables

- `speech.js`, `VoiceButton.razor`, composer wiring, i18n ×8, testes

## 8. Referências

- OpenWebUiCode `Components/VoiceButton.razor` (estados/erros)
- `ChatComposer.razor` (local do botão)

## 9. Guardrails

- Sem dependência de serviço externo; falha da API nunca quebra o
  composer.
- Áudio nunca sai do browser (API nativa).
- i18n obrigatório; `dotnet format`/drift guards verdes.

## 10. DoD

- [ ] Dita transcreve pro input editável; estados/erros i18n.
- [ ] Fallback honesto sem SpeechRecognition.
- [ ] Testes + guards verdes.
