/* =========================================================================
   DPT.Server - soft synthesized notification sounds (WebAudio, no files).
   Nothing plays before the first user gesture (browser autoplay policy).
   ========================================================================= */
(function (global) {
  'use strict';

  var ctx = null;
  var master = null;
  var unlocked = false;
  var enabled = true;
  var volume = 0.55;

  function ensure() {
    if (ctx) return ctx;
    var AC = global.AudioContext || global.webkitAudioContext;
    if (!AC) return null;
    try {
      ctx = new AC();
      master = ctx.createGain();
      master.gain.value = volume;
      master.connect(ctx.destination);
    } catch (e) { ctx = null; }
    return ctx;
  }

  function unlock() {
    var c = ensure();
    if (!c) return;
    if (c.state === 'suspended') { try { c.resume(); } catch (e) {} }
    unlocked = true;
  }

  /** A warm two-oscillator blip. */
  function tone(freq, start, dur, gain, type, slideTo) {
    var c = ensure();
    if (!c || !master) return;
    var t0 = c.currentTime + start;
    var osc = c.createOscillator();
    var osc2 = c.createOscillator();
    var g = c.createGain();
    var filter = c.createBiquadFilter();

    osc.type = type || 'sine';
    osc2.type = 'triangle';
    osc.frequency.setValueAtTime(freq, t0);
    osc2.frequency.setValueAtTime(freq * 2.005, t0);
    if (slideTo) {
      osc.frequency.exponentialRampToValueAtTime(slideTo, t0 + dur);
      osc2.frequency.exponentialRampToValueAtTime(slideTo * 2.005, t0 + dur);
    }

    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(4200, t0);
    filter.Q.value = 0.6;

    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(Math.max(0.0002, gain), t0 + 0.012);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

    osc.connect(filter); osc2.connect(filter);
    filter.connect(g); g.connect(master);
    osc.start(t0); osc2.start(t0);
    osc.stop(t0 + dur + 0.02); osc2.stop(t0 + dur + 0.02);
  }

  var presets = {
    connect:    function () { tone(523.25, 0, 0.16, 0.18, 'sine'); tone(783.99, 0.09, 0.22, 0.14, 'sine'); },
    disconnect: function () { tone(392.00, 0, 0.20, 0.16, 'sine'); tone(261.63, 0.10, 0.28, 0.14, 'sine'); },
    join:       function () { tone(659.25, 0, 0.13, 0.14, 'sine'); tone(987.77, 0.07, 0.18, 0.10, 'sine'); },
    leave:      function () { tone(659.25, 0, 0.12, 0.12, 'sine', 440); },
    message:    function () { tone(880.00, 0, 0.09, 0.10, 'sine'); },
    notify:     function () { tone(740.00, 0, 0.10, 0.12, 'sine'); tone(1108.73, 0.06, 0.16, 0.08, 'sine'); },
    error:      function () { tone(220.00, 0, 0.18, 0.16, 'triangle', 174.61); },
    kick:       function () { tone(311.13, 0, 0.16, 0.16, 'triangle', 155.56); },
    reward:     function () { tone(659.25, 0, 0.10, 0.12, 'sine'); tone(830.61, 0.07, 0.12, 0.12, 'sine'); tone(1046.50, 0.14, 0.22, 0.12, 'sine'); },
    start:      function () { tone(392.00, 0, 0.18, 0.12, 'sine'); tone(587.33, 0.10, 0.20, 0.12, 'sine'); tone(783.99, 0.20, 0.30, 0.12, 'sine'); }
  };

  var Sound = {
    /** user gesture hook */
    unlock: unlock,

    setEnabled: function (on) { enabled = !!on; if (enabled) unlock(); },
    isEnabled: function () { return enabled; },

    setVolume: function (v) {
      volume = Math.max(0, Math.min(1, Number(v) || 0));
      if (master) master.gain.value = volume;
    },
    getVolume: function () { return volume; },

    play: function (name) {
      if (!enabled) return;
      var f = presets[name];
      if (!f) return;
      if (!ctx) ensure();
      if (!ctx) return;
      try { f(); } catch (e) { }
    },

    /** Tiny click for UI interactions (very quiet). */
    click: function () { if (enabled) tone(1046.5, 0, 0.05, 0.05, 'sine'); }
  };

  global.Sound = Sound;

  // Unlock on the first pointer/key event anywhere.
  ['pointerdown', 'keydown', 'touchstart'].forEach(function (evt) {
    document.addEventListener(evt, function once() {
      unlock();
      document.removeEventListener(evt, once);
    }, false);
  });
})(this);