// SPEC-159: ditado por voz (STT) — Web Speech API, browser-only.
// Áudio nunca sai do browser; falha da API nunca quebra o composer.
(function () {
    'use strict';
    var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    var recog = null;
    var ref = null;
    var accFinal = '';

    window.scSpeech = {
        supported: function () { return !!SR; },

        // Inicia o ditado. Callbacks no ref: OnSttResult(text),
        // OnSttEnded(), OnSttError(code). Retorna false sem suporte/falha.
        start: function (dotnetRef, lang) {
            if (!SR) { return false; }
            window.scSpeech.stop();
            ref = dotnetRef;
            accFinal = '';
            try {
                recog = new SR();
            } catch (e) {
                ref = null;
                return false;
            }
            recog.lang = lang || 'en-US';
            recog.interimResults = true;
            recog.continuous = true;
            recog.onresult = function (e) {
                var interim = '';
                for (var i = e.resultIndex; i < e.results.length; i++) {
                    var r = e.results[i];
                    if (r.isFinal) {
                        accFinal += (accFinal ? ' ' : '') + r[0].transcript;
                    } else {
                        interim += r[0].transcript;
                    }
                }
                var full = accFinal;
                if (interim) { full += (full ? ' ' : '') + interim; }
                if (ref) { ref.invokeMethodAsync('OnSttResult', full); }
            };
            recog.onerror = function (e) {
                if (ref) {
                    ref.invokeMethodAsync('OnSttError', e.error || 'error');
                }
            };
            recog.onend = function () {
                var d = ref;
                ref = null;
                recog = null;
                if (d) { d.invokeMethodAsync('OnSttEnded'); }
            };
            try {
                recog.start();
                return true;
            } catch (e) {
                ref = null;
                recog = null;
                return false;
            }
        },

        stop: function () {
            try {
                if (recog) { recog.stop(); }
            } catch (e) { /* noop — já parado */ }
        }
    };
})();
