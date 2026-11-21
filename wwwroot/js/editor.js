/**
 * svacitra - 3-Tab Non-Destructive Edit Studio
 * Basic Fixes, Tuning, Effects, Interactive Crop, and Real-Time Preview.
 * Integrates GPT-6 Luna AI Auto-Tune.
 */

window.PicasaEditor = {
  currentPhoto: null,
  edits: {},
  originalEdits: {},
  isCropActive: false,
  cropRect: { x: 0.1, y: 0.1, w: 0.8, h: 0.8 },
  isComparing: false,

  init() {
    window.PhotoDesign.init(this);
    this.setupTabNavigation();
    this.setupBasicFixes();
    this.setupTuningSliders();
    this.setupEngineSliders();
    this.setupEffects();
    this.setupCropOverlay();
    this.setupStageControls();
    this.setupAIPrompt();
  },

  setupTabNavigation() {
    document.querySelectorAll('.edit-tab').forEach(tab => {
      tab.addEventListener('click', () => {
        document.querySelectorAll('.edit-tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.edit-tab-content').forEach(c => c.classList.remove('active'));

        tab.classList.add('active');
        const target = document.getElementById(`edit-tab-${tab.dataset.tab}`);
        if (target) target.classList.add('active');
      });
    });
  },

  setupBasicFixes() {
    // GPT-6 Luna AI Auto-Tune
    const lunaBtn = document.getElementById('btn-tool-luna-autotune');
    if (lunaBtn) {
      lunaBtn.addEventListener('click', async () => {
        if (!this.currentPhoto) return;
        lunaBtn.innerHTML = '<span class="tool-icon">⏳</span> Analyzing with Luna AI...';

        try {
          const res = await fetch('/api/ai/analyze', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ photo_id: this.currentPhoto.id })
          });

          let data;
          try {
            data = await res.json();
          } catch (jsonErr) {
            const raw = await res.text().catch(() => '');
            throw new Error(raw || 'Failed to parse response');
          }

          let targetResult = null;
          if (data.success && data.result) {
            targetResult = data.result;
          } else if (!data.success) {
            const errorMsg = data.error || 'AI analysis failed.';
            const useFallback = confirm(`${errorMsg}\n\nWould you like to auto-tune using Luna's built-in offline Smart Heuristic engine instead?`);
            if (useFallback) {
              const fRes = await fetch('/api/ai/analyze', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ photo_id: this.currentPhoto.id, fallback_heuristic: true })
              });
              const fData = await fRes.json();
              if (fData.success && fData.result) {
                targetResult = fData.result;
              }
            }
          }

          if (targetResult) {
            const fixes = targetResult.fixes || {};
            // Apply fixes
            if (fixes.brightness !== undefined) this.edits.brightness = fixes.brightness;
            if (fixes.contrast !== undefined) this.edits.contrast = fixes.contrast;
            if (fixes.fill_light !== undefined) this.edits.fill_light = fixes.fill_light;
            if (fixes.highlights !== undefined) this.edits.highlights = fixes.highlights;
            if (fixes.shadows !== undefined) this.edits.shadows = fixes.shadows;
            if (fixes.warmth !== undefined) this.edits.warmth = fixes.warmth;
            if (fixes.saturation !== undefined) this.edits.saturation = fixes.saturation;

            this.syncSliderUI();
            this.applyLivePreview();

            // Display critique
            const critiqueBox = document.getElementById('luna-critique-box');
            const critiqueText = document.getElementById('luna-critique-text');
            if (critiqueBox && critiqueText) {
              critiqueBox.style.display = 'block';
              critiqueText.innerText = targetResult.critique || targetResult.caption || 'Optimal tone adjustments applied.';
            }

            this.currentPhoto.ai_processed = true;
            this.currentPhoto.ai_tags = targetResult.tags;
            this.currentPhoto.ai_caption = targetResult.caption;
            window.Picasa.emit('photoUpdated', this.currentPhoto);
          }
        } catch (e) {
          alert('Luna AI analysis: ' + (e.message || e));
        } finally {
          lunaBtn.innerHTML = '<span class="tool-icon">✨</span> GPT-6 Luna AI Auto-Tune';
        }
      });
    }

    // Crop Tool
    const cropBtn = document.getElementById('btn-tool-crop');
    const cropSubpanel = document.getElementById('crop-subpanel');
    cropBtn.addEventListener('click', () => {
      this.isCropActive = !this.isCropActive;
      cropBtn.classList.toggle('active', this.isCropActive);
      cropSubpanel.style.display = this.isCropActive ? 'block' : 'none';
      document.getElementById('crop-overlay').style.display = this.isCropActive ? 'block' : 'none';
      if (this.isCropActive) {
        // Start from the crop that is already applied, if any.
        const c = this.edits.crop;
        this.cropRect = Array.isArray(c) && c.length === 4 ? { x: c[0], y: c[1], w: c[2], h: c[3] } : { x: 0.1, y: 0.1, w: 0.8, h: 0.8 };
        this.updateCropOverlayStyle();
      }
      this.applyLivePreview();
    });

    // Straighten Tool
    const strBtn = document.getElementById('btn-tool-straighten');
    const strSubpanel = document.getElementById('straighten-subpanel');
    strBtn.addEventListener('click', () => {
      const isVisible = strSubpanel.style.display === 'block';
      strSubpanel.style.display = isVisible ? 'none' : 'block';
      strBtn.classList.toggle('active', !isVisible);
    });

    const strSlider = document.getElementById('slider-straighten');
    const strVal = document.getElementById('val-straighten');
    strSlider.addEventListener('input', (e) => {
      const val = parseFloat(e.target.value);
      strVal.innerText = `${val > 0 ? '+' : ''}${val}°`;
      this.edits.straighten = val;
      this.applyLivePreview();
    });

    document.getElementById('btn-reset-straighten').addEventListener('click', () => {
      strSlider.value = 0;
      strVal.innerText = '0°';
      this.edits.straighten = 0;
      this.applyLivePreview();
    });

    document.getElementById('btn-apply-straighten').addEventListener('click', () => {
      strSubpanel.style.display = 'none';
      strBtn.classList.remove('active');
    });

    // Red-eye
    const redEyeBtn = document.getElementById('btn-tool-redeye');
    redEyeBtn.addEventListener('click', () => {
      this.edits.redeye = !this.edits.redeye;
      redEyeBtn.classList.toggle('active', this.edits.redeye);
      // Real pixel fix (OpenCV): the server re-renders the base image with the red pupils corrected.
      this.reloadBaseImage();
    });

    // I'm Feeling Lucky
    const luckyBtn = document.getElementById('btn-tool-lucky');
    luckyBtn.addEventListener('click', () => {
      this.edits.feeling_lucky = !this.edits.feeling_lucky;
      luckyBtn.classList.toggle('active', this.edits.feeling_lucky);
      // The flag itself is the edit: preview and server both add +15 contrast, +18 saturation,
      // +5 brightness and +5 warmth on top of the sliders while it is on (see effectiveEdits()).
      this.applyLivePreview();
    });

    // Auto Contrast
    document.getElementById('btn-tool-autocontrast').addEventListener('click', () => {
      this.edits.contrast = 20;
      this.syncSliderUI();
      this.applyLivePreview();
    });

    // Auto Color
    document.getElementById('btn-tool-autocolor').addEventListener('click', () => {
      this.edits.saturation = 22;
      this.edits.warmth = 8;
      this.syncSliderUI();
      this.applyLivePreview();
    });

    // Fill Light slider
    const fillSlider = document.getElementById('slider-fill-light');
    const fillVal = document.getElementById('val-fill-light');
    fillSlider.addEventListener('input', (e) => {
      const val = parseInt(e.target.value, 10);
      fillVal.innerText = val;
      this.edits.fill_light = val;
      this.applyLivePreview();
    });
  },

  setupTuningSliders() {
    const sliders = [
      { id: 'slider-brightness', key: 'brightness', valId: 'val-brightness' },
      { id: 'slider-highlights', key: 'highlights', valId: 'val-highlights' },
      { id: 'slider-shadows', key: 'shadows', valId: 'val-shadows' },
      { id: 'slider-contrast', key: 'contrast', valId: 'val-contrast' },
      { id: 'slider-warmth', key: 'warmth', valId: 'val-warmth' },
      { id: 'slider-saturation', key: 'saturation', valId: 'val-saturation' }
    ];

    sliders.forEach(({ id, key, valId }) => {
      const el = document.getElementById(id);
      const valEl = document.getElementById(valId);
      el.addEventListener('input', (e) => {
        const val = parseInt(e.target.value, 10);
        valEl.innerText = `${val > 0 ? '+' : ''}${val}`;
        this.edits[key] = val;
        this.applyLivePreview();
      });
    });
  },

  /** OpenCV sliders: no CSS equivalent, so the server re-renders the base image when the slider is released. */
  setupEngineSliders() {
    ['denoise', 'clarity', 'detail'].forEach(key => {
      const el = document.getElementById('slider-' + key);
      const valEl = document.getElementById('val-' + key);
      if (!el) return;
      el.addEventListener('input', (e) => { valEl.innerText = e.target.value; });
      el.addEventListener('change', (e) => {
        this.edits[key] = parseInt(e.target.value, 10) || 0;
        this.reloadBaseImage();
      });
    });
  },

  setupEffects() {
    document.querySelectorAll('.effect-card').forEach(card => {
      card.addEventListener('click', () => {
        document.querySelectorAll('.effect-card').forEach(c => c.classList.remove('active'));
        card.classList.add('active');

        const effect = card.dataset.effect;
        this.edits.effect = effect;
        // The vignette card switches the vignette on (default strength 50); any other card switches it off.
        this.edits.vignette = effect === 'vignette' ? (this.edits.vignette || 50) : 0;

        this.syncSliderUI();
        this.applyLivePreview();
      });
    });

    const vigSlider = document.getElementById('slider-vignette');
    const vigVal = document.getElementById('val-vignette');
    vigSlider.addEventListener('input', (e) => {
      const val = parseInt(e.target.value, 10);
      vigVal.innerText = val;
      this.edits.vignette = val;
      this.applyLivePreview();
    });
  },

  setupCropOverlay() {
    const overlay = document.getElementById('crop-overlay');
    const applyBtn = document.getElementById('btn-apply-crop');
    const cancelBtn = document.getElementById('btn-cancel-crop');

    document.querySelectorAll('.aspect-pills .pill-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.aspect-pills .pill-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        const ratio = btn.dataset.ratio;
        this.applyAspectPreset(ratio);
      });
    });

    applyBtn.addEventListener('click', () => {
      this.edits.crop = [this.cropRect.x, this.cropRect.y, this.cropRect.w, this.cropRect.h];
      this.isCropActive = false;
      overlay.style.display = 'none';
      document.getElementById('crop-subpanel').style.display = 'none';
      document.getElementById('btn-tool-crop').classList.remove('active');
      this.applyLivePreview();
    });

    cancelBtn.addEventListener('click', () => {
      this.isCropActive = false;
      overlay.style.display = 'none';
      document.getElementById('crop-subpanel').style.display = 'none';
      document.getElementById('btn-tool-crop').classList.remove('active');
      this.applyLivePreview();
    });
  },

  applyAspectPreset(ratio) {
    if (ratio === '1:1') {
      this.cropRect = { x: 0.2, y: 0.1, w: 0.6, h: 0.6 };
    } else if (ratio === '4:3') {
      this.cropRect = { x: 0.1, y: 0.15, w: 0.8, h: 0.6 };
    } else if (ratio === '16:9') {
      this.cropRect = { x: 0.05, y: 0.25, w: 0.9, h: 0.5 };
    } else if (ratio === '4:6') {
      this.cropRect = { x: 0.2, y: 0.05, w: 0.6, h: 0.9 };
    } else {
      this.cropRect = { x: 0.1, y: 0.1, w: 0.8, h: 0.8 };
    }
    this.updateCropOverlayStyle();
  },

  updateCropOverlayStyle() {
    const overlay = document.getElementById('crop-overlay');
    overlay.style.left = `${this.cropRect.x * 100}%`;
    overlay.style.top = `${this.cropRect.y * 100}%`;
    overlay.style.width = `${this.cropRect.w * 100}%`;
    overlay.style.height = `${this.cropRect.h * 100}%`;
  },

  setupStageControls() {
    const compareBtn = document.getElementById('btn-compare-toggle');
    const startCompare = () => {
      this.isComparing = true;
      this.applyLivePreview(true);
      compareBtn.classList.add('active');
    };
    const endCompare = () => {
      this.isComparing = false;
      this.applyLivePreview(false);
      compareBtn.classList.remove('active');
    };

    compareBtn.addEventListener('mousedown', startCompare);
    window.addEventListener('mouseup', () => {
      if (this.isComparing) endCompare();
    });

    document.getElementById('btn-rotate-edit-left').addEventListener('click', () => {
      this.edits.rotation = ((this.edits.rotation || 0) - 90) % 360;
      this.reloadBaseImage();
    });
    document.getElementById('btn-rotate-edit-right').addEventListener('click', () => {
      this.edits.rotation = ((this.edits.rotation || 0) + 90) % 360;
      this.reloadBaseImage();
    });

    document.getElementById('btn-save-edits').addEventListener('click', async () => {
      if (!this.currentPhoto) return;
      const photo = this.currentPhoto;
      const edits = structuredClone(this.edits);
      try {
        const response = await fetch(`/api/photo/${photo.id}/save-edit`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ edits })
        });
        if (!response.ok) throw new Error("Could not save these edits.");
        photo.edits = edits;
        window.Picasa.emit('photoUpdated', photo);
        alert('Edits saved non-destructively to photo recipe!');
      } catch (e) {
        alert('Save failed: ' + e);
      }
    });

    document.getElementById('btn-undo-all').addEventListener('click', () => {
      const hadEngine = !!(this.edits.rotation || this.edits.redeye || this.edits.denoise || this.edits.clarity || this.edits.detail);
      this.edits = {};
      document.getElementById('btn-tool-redeye').classList.remove('active');
      if (hadEngine) this.reloadBaseImage();
      const critiqueBox = document.getElementById('luna-critique-box');
      if (critiqueBox) critiqueBox.style.display = 'none';
      const resultBox = document.getElementById('luna-prompt-result-box');
      if (resultBox) resultBox.style.display = 'none';
      this.syncSliderUI();
      this.applyLivePreview();
    });

    document.getElementById('btn-export-edited').addEventListener('click', async () => {
      if (!this.currentPhoto) return;
      const photo = this.currentPhoto;
      const edits = structuredClone(this.edits);
      try {
        const response = await fetch('/api/photo/' + photo.id + '/save-edit', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ edits })
        });
        if (!response.ok) throw new Error();
        photo.edits = edits;
        window.Picasa.emit('photoUpdated', photo);
        if (photo !== this.currentPhoto) return;
      } catch {
        alert('Could not save the design for export.');
        return;
      }
      window.PicasaTray.selectSingle(this.currentPhoto);
      window.PicasaTray.exportActive();
    });
  },

  setupAIPrompt() {
    const toggle = document.getElementById('prompt-view-toggle');
    toggle.addEventListener('click', () => this.setSplitView(toggle.getAttribute('aria-pressed') !== 'true'));
    const promptInput = document.getElementById('luna-prompt-input');
    const runBtn = document.getElementById('btn-luna-prompt-run');
    const resultBox = document.getElementById('luna-prompt-result-box');
    const closeResultBtn = document.getElementById('btn-close-luna-prompt-result');

    if (closeResultBtn) {
      closeResultBtn.addEventListener('click', () => {
        if (resultBox) resultBox.style.display = 'none';
      });
    }

    // Quick prompt chips
    document.querySelectorAll('.luna-chip').forEach(chip => {
      chip.addEventListener('click', () => {
        const text = chip.dataset.prompt;
        if (promptInput) {
          promptInput.value = text;
        }
        this.executeAIPrompt(text);
      });
    });

    // Run button
    if (runBtn) {
      runBtn.addEventListener('click', () => {
        const text = promptInput ? promptInput.value.trim() : '';
        if (text) {
          this.executeAIPrompt(text);
        } else if (promptInput) {
          promptInput.focus();
        }
      });
    }

    // Enter key inside prompt field
    if (promptInput) {
      promptInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          const text = promptInput.value.trim();
          if (text) {
            this.executeAIPrompt(text);
          }
        }
      });
    }
  },

  setSplitView(enabled) {
    document.getElementById('canvas-viewport').classList.toggle('prompt-split', enabled);
    const toggle = document.getElementById('prompt-view-toggle');
    toggle.setAttribute('aria-pressed', String(enabled));
    toggle.textContent = enabled ? 'Single view' : 'Side by side';
    toggle.hidden = !this.comparisonSession;
    document.getElementById('btn-compare-toggle').hidden = enabled;
  },

  clearPromptComparison() {
    this.comparisonSession?.abort();
    this.comparisonSession = null;
    this.setSplitView(false);
    const img = document.getElementById('prompt-before-img');
    if (img.src.startsWith('blob:')) URL.revokeObjectURL(img.src);
    img.removeAttribute('src');
    img.hidden = true;
  },

  async showPromptComparison(before) {
    this.clearPromptComparison();
    const session = this.comparisonSession = new AbortController();
    this.setSplitView(true);
    const img = document.getElementById('prompt-before-img');
    const status = document.getElementById('prompt-before-status');
    status.textContent = 'Loading preview…';
    try {
      const res = await fetch('/api/photo/' + before.id + '/prompt-preview', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ edits: before.edits }), signal: session.signal
      });
      if (!res.ok) throw new Error();
      const blob = await res.blob();
      if (session.signal.aborted) return;
      img.src = URL.createObjectURL(blob);
      await img.decode();
      if (session.signal.aborted) return;
      img.hidden = false;
      status.textContent = '';
    } catch {
      if (!session.signal.aborted) status.textContent = 'Before preview unavailable';
    }
  },

  async executeAIPrompt(userPrompt, forceHeuristic = false) {
    if (this.promptBusy) return;
    if (!this.currentPhoto) {
      alert('Please open a photo in Edit Studio first.');
      return;
    }

    const before = { id: this.currentPhoto.id, edits: structuredClone(this.edits) };
    const generation = this.photoGeneration;
    this.promptBusy = true;

    // With an image model (GPT-Image-2.5) and an API key, the prompt generates a new picture in the cloud.
    const ai = window.Picasa.state.aiSettings || {};
    const generative = !forceHeuristic && !!ai.api_key && /^gpt-image/i.test(ai.model || '');
    // No confirmation: every run saves a new copy next to the original (name_luna.jpg, name_luna_2.jpg, ...).

    const runBtn = document.getElementById('btn-luna-prompt-run');
    const statusBox = document.getElementById('luna-prompt-status');
    const statusMsg = document.getElementById('luna-prompt-status-msg');
    const resultBox = document.getElementById('luna-prompt-result-box');
    const explanationEl = document.getElementById('luna-prompt-explanation');
    const modelBadge = document.getElementById('luna-prompt-model-badge');

    if (runBtn) runBtn.disabled = true;
    if (resultBox) resultBox.style.display = 'none';
    if (statusBox) {
      statusBox.style.display = 'flex';
      if (statusMsg) statusMsg.innerText = forceHeuristic ? 'Processing with offline heuristic engine...'
        : generative ? 'Luna is generating an AI edit (this can take a minute)...' : 'Luna is analyzing and tuning photo...';
    }

    try {
      const res = await fetch('/api/ai/prompt-tune', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          photo_id: before.id,
          edits: before.edits,
          prompt: userPrompt,
          fallback_heuristic: forceHeuristic
        })
      });

      let data;
      try {
        data = await res.json();
      } catch (jsonErr) {
        const raw = await res.text().catch(() => '');
        throw new Error(raw || 'Failed to parse response');
      }

      if (generation !== this.photoGeneration || window.Picasa.state.currentTab !== 'editor') return;
      if (JSON.stringify(this.edits) !== JSON.stringify(before.edits)) {
        alert(data.result?.generated ? 'Your AI copy is in the library. Your current edits are still open.'
          : 'The photo changed while the prompt was running. Run the prompt again to compare your latest edits.');
        return;
      }
      let targetResult = null;
      if (data.success && data.result && data.result.generated && data.photo) {
        // Image model: a new AI-edited copy was created. Add it to the library view and open it.
        const photos = window.Picasa.state.photos;
        const at = photos.findIndex(p => p.id === this.currentPhoto.id);
        if (!photos.some(p => p.id === data.photo.id)) photos.splice(at < 0 ? 0 : at + 1, 0, data.photo);
        this.openPhoto(data.photo);
        this.showPromptComparison(before);
        if (resultBox && explanationEl) {
          resultBox.style.display = 'flex';
          explanationEl.innerText = data.result.explanation;
          if (modelBadge) modelBadge.innerText = `Luna (${data.result.model})`;
        }
        if (window.PicasaLibrary) window.PicasaLibrary.loadSummary().catch(() => {});
        return;
      }
      if (data.success && data.result) {
        targetResult = data.result;
      } else if (!data.success) {
        if (data.fallback) {
          const errorMsg = data.error || 'AI service error.';
          const useFallback = confirm(`${errorMsg}\n\nWould you like to tune using Luna's built-in offline Smart Heuristic engine instead?`);
          if (useFallback) {
            targetResult = data.fallback;
          }
        } else {
          throw new Error(data.error || 'Prompt tuning failed');
        }
      }

      if (targetResult) {
        const fixes = targetResult.fixes || {};
        if (fixes.brightness !== undefined) this.edits.brightness = fixes.brightness;
        if (fixes.contrast !== undefined) this.edits.contrast = fixes.contrast;
        if (fixes.fill_light !== undefined) this.edits.fill_light = fixes.fill_light;
        if (fixes.highlights !== undefined) this.edits.highlights = fixes.highlights;
        if (fixes.shadows !== undefined) this.edits.shadows = fixes.shadows;
        if (fixes.warmth !== undefined) this.edits.warmth = fixes.warmth;
        if (fixes.saturation !== undefined) this.edits.saturation = fixes.saturation;
        if (fixes.vignette !== undefined) this.edits.vignette = fixes.vignette;

        if (targetResult.effect && targetResult.effect !== 'none') {
          this.edits.effect = targetResult.effect;
        }

        // Synchronize UI sliders and effect cards
        this.syncSliderUI();
        this.applyLivePreview();

        this.showPromptComparison(before);

        // Render response feedback
        if (resultBox && explanationEl) {
          resultBox.style.display = 'flex';
          explanationEl.innerText = targetResult.explanation || `Tuned photo for: "${userPrompt}"`;
          if (modelBadge) {
            modelBadge.innerText = targetResult.model ? `Luna (${targetResult.model})` : 'GPT-6 Luna';
          }
        }
      }
    } catch (err) {
      alert('AI Prompt Tuning: ' + (err.message || err));
    } finally {
      this.promptBusy = false;
      if (runBtn) runBtn.disabled = false;
      if (statusBox) statusBox.style.display = 'none';
    }
  },

  openPhoto(photo) {
    window.ComparePicks.close();
    document.getElementById("copy-edit-panel").open = false;
    window.PhotoDesign.reset();
    this.photoGeneration = (this.photoGeneration || 0) + 1;
    this.clearPromptComparison();
    this.currentPhoto = photo;
    this.edits = JSON.parse(JSON.stringify(photo.edits || {}));
    this.originalEdits = JSON.parse(JSON.stringify(this.edits));

    // Keep the tray selection on the photo being edited, so Export / Star / Delete act on what is on screen
    // (not on whatever was selected in the library, or on the original of an AI-generated copy).
    if (!window.Picasa.state.isHeld && window.PicasaTray) {
      window.Picasa.state.selectedPhotos = [photo];
      window.PicasaTray.updateTrayUI();
    }

    window.Picasa.switchTab('editor');

    // raw=1: the base image carries no recipe, because the live preview below applies the edits itself
    // (otherwise saved edits would be applied twice). Untouched photos stream straight from disk.
    this.loadBaseImage(true);

    this.isCropActive = false;
    document.getElementById('crop-overlay').style.display = 'none';
    document.getElementById('crop-subpanel').style.display = 'none';
    document.getElementById('btn-tool-crop').classList.remove('active');
    document.getElementById('btn-tool-redeye').classList.toggle('active', !!this.edits.redeye);

    const critiqueBox = document.getElementById('luna-critique-box');
    const critiqueText = document.getElementById('luna-critique-text');
    if (photo.ai_caption && critiqueBox && critiqueText) {
      critiqueBox.style.display = 'block';
      critiqueText.innerText = photo.ai_caption;
    } else if (critiqueBox) {
      critiqueBox.style.display = 'none';
    }

    const resultBox = document.getElementById('luna-prompt-result-box');
    if (resultBox) resultBox.style.display = 'none';

    this.syncSliderUI();
    this.applyLivePreview();
    this.renderFilmstrip();
  },

  syncSliderUI() {
    window.PhotoDesign.sync();
    const e = this.edits;
    const setVal = (id, valId, val) => {
      const el = document.getElementById(id);
      const vEl = document.getElementById(valId);
      if (el) el.value = val || 0;
      if (vEl) vEl.innerText = val ? `${val > 0 ? '+' : ''}${val}` : '0';
    };

    setVal('slider-fill-light', 'val-fill-light', e.fill_light);
    setVal('slider-straighten', 'val-straighten', e.straighten);
    setVal('slider-brightness', 'val-brightness', e.brightness);
    setVal('slider-highlights', 'val-highlights', e.highlights);
    setVal('slider-shadows', 'val-shadows', e.shadows);
    setVal('slider-contrast', 'val-contrast', e.contrast);
    setVal('slider-warmth', 'val-warmth', e.warmth);
    setVal('slider-saturation', 'val-saturation', e.saturation);
    const eff = e.effect || 'none';
    const vignetteOn = eff === 'vignette' || (e.vignette || 0) > 0;
    setVal('slider-vignette', 'val-vignette', e.vignette ?? (eff === 'vignette' ? 50 : 0));
    const vigControls = document.getElementById('vignette-controls');
    if (vigControls) vigControls.style.display = vignetteOn ? 'block' : 'none';

    document.querySelectorAll('.effect-card').forEach(c => {
      c.classList.toggle('active', c.dataset.effect === eff);
    });

    document.getElementById('btn-tool-lucky').classList.toggle('active', !!e.feeling_lucky);
    document.getElementById('btn-tool-redeye').classList.toggle('active', !!e.redeye);

    ['denoise', 'clarity', 'detail'].forEach(key => {
      const el = document.getElementById('slider-' + key);
      const vEl = document.getElementById('val-' + key);
      if (el) el.value = e[key] || 0;
      if (vEl) vEl.innerText = String(e[key] || 0);
    });
    // Undo, AI results and similar change the OpenCV settings without touching a slider: refresh the base image.
    if (this.currentPhoto && this.baseKey !== undefined && this.engineKey() !== this.baseKey) this.reloadBaseImage();
  },

  /** The part of the recipe the server bakes into the base image (rotation + OpenCV operations). */
  engineKey() {
    const e = this.edits;
    const rot = (((e.rotation || 0) % 360) + 360) % 360;
    let key = `rot=${rot}`;
    if (e.redeye) key += '&redeye=1';
    ['denoise', 'clarity', 'detail'].forEach(k => { if (e[k] > 0) key += `&${k}=${Math.round(e[k])}`; });
    return key;
  },

  /** Slider values with the "I'm Feeling Lucky" boost folded in (mirrors EditRecipe.WithLuckyApplied in C#). */
  effectiveEdits() {
    const e = { ...this.edits };
    if (e.feeling_lucky) {
      e.contrast = (e.contrast || 0) + 15;
      e.saturation = (e.saturation || 0) + 18;
      e.brightness = (e.brightness || 0) + 5;
      e.warmth = (e.warmth || 0) + 5;
    }
    return e;
  },

  /** Base image for the canvas: no recipe (raw=1), but 90-degree rotation is done by the server. */
  baseImageUrl() {
    this.baseKey = this.engineKey();
    return `/api/image/${this.currentPhoto.id}?max_dim=1920&raw=1&${this.baseKey}&t=${Date.now()}`;
  },

  reloadBaseImage() {
    this.loadBaseImage(false);
  },

  /**
   * Fetches the base image in the background and swaps it in when it is ready. A large original on a slow
   * or cloud-synced drive can take a long time the first time, so with `placeholder` the cached thumbnail of
   * the new photo is shown at once (instead of leaving the previous photo on screen) with a loading note.
   */
  loadBaseImage(placeholder) {
    if (!this.currentPhoto) return;
    const img = document.getElementById('editor-display-img');
    const photo = this.currentPhoto;
    const url = this.baseImageUrl();
    const token = (this.loadToken = (this.loadToken || 0) + 1);

    let note = document.getElementById('editor-loading-note');
    if (!note) {
      note = document.createElement('div');
      note.id = 'editor-loading-note';
      note.className = 'editor-loading-note';
      img.parentNode.appendChild(note);
    }
    note.textContent = placeholder ? 'Loading full image...' : 'Rendering...';
    note.style.display = 'none';
    let pending = true;
    setTimeout(() => { if (pending && token === this.loadToken) note.style.display = 'block'; }, 350);

    img.classList.add('engine-busy');
    img.onload = () => this.applyLivePreview();
    if (placeholder) {
      // The thumbnail already has the saved edits baked in, so the live-preview filters are switched off for it.
      img.classList.add('is-placeholder');
      img.src = `/api/thumb/${photo.id}?t=${Date.now()}`;
    }

    const finish = () => {
      pending = false;
      note.style.display = 'none';
      img.classList.remove('engine-busy');
    };
    fetch(url)
      .then(res => {
        const type = res.headers.get('content-type') || '';
        if (!res.ok || !type.startsWith('image/')) throw new Error('HTTP ' + res.status);
        return res.blob();
      })
      .then(blob => {
        if (token !== this.loadToken) return; // the user already moved on to another photo or setting
        const objectUrl = URL.createObjectURL(blob);
        const previous = this.baseObjectUrl;
        this.baseObjectUrl = objectUrl;
        img.onload = () => {
          if (previous) URL.revokeObjectURL(previous);
          this.applyLivePreview();
        };
        img.classList.remove('is-placeholder');
        img.src = objectUrl;
        finish();
        this.prefetchNeighbours(photo);
      })
      .catch(err => {
        if (token !== this.loadToken) return;
        finish();
        note.textContent = 'Could not load the full image (' + (err.message || err) + ').';
        note.style.display = 'block';
      });
  },

  /** Warms the server's preview cache for the photos next to the current one, so stepping through is quick. */
  prefetchNeighbours(photo) {
    const photos = window.Picasa.state.photos || [];
    const at = photos.findIndex(p => p.id === photo.id);
    if (at < 0) return;
    [photos[at + 1], photos[at - 1]].forEach(p => {
      if (!p || (this.prefetched = this.prefetched || new Set()).has(p.id)) return;
      this.prefetched.add(p.id);
      const rot = ((((p.edits || {}).rotation || 0) % 360) + 360) % 360;
      fetch(`/api/image/${p.id}?max_dim=1920&raw=1&rot=${rot}&warm=1`).then(r => r.blob()).catch(() => {});
    });
  },

  /** Shadows/highlights tone curve; identical to ToneMath.ToneCurve in C#. */
  toneCurve(v, shadows, highlights) {
    const k = 27 / 4;
    const fs = v * (1 - v) * (1 - v) * k;
    const fh = v * v * (1 - v) * k;
    return Math.min(1, Math.max(0, v + 0.25 * (shadows / 100) * fs + 0.25 * (highlights / 100) * fh));
  },

  /**
   * Real-time, non-destructive preview. CSS filter functions cover brightness/contrast/saturation/effects,
   * a small inline SVG filter adds the shadows/highlights curve, sharpening and cool white balance, and
   * the wrapper carries crop (clip-path) and vignette (mask). The server renderer uses the same math.
   */
  applyLivePreview(forceOriginal = false) {
    window.PhotoDesign.preview(!forceOriginal && !this.isComparing && !this.isCropActive);
    const img = document.getElementById('editor-display-img');
    if (!img) return;
    // The thumbnail placeholder already shows the saved edits (crop included): add nothing on top of it.
    if (img.classList.contains('is-placeholder')) forceOriginal = true;
    const wrapper = document.getElementById('canvas-image-wrapper');
    const setMask = (value, size, position) => {
      wrapper.style.webkitMaskImage = value;
      wrapper.style.maskImage = value;
      wrapper.style.webkitMaskSize = size;
      wrapper.style.maskSize = size;
      wrapper.style.webkitMaskPosition = position;
      wrapper.style.maskPosition = position;
      wrapper.style.webkitMaskRepeat = 'no-repeat';
      wrapper.style.maskRepeat = 'no-repeat';
    };

    if (forceOriginal) {
      img.style.filter = 'none';
      img.style.transform = 'none';
      wrapper.style.clipPath = 'none';
      setMask('none', 'auto', '0 0');
      return;
    }

    const e = this.effectiveEdits();

    const brightness = 1.0 + ((e.brightness || 0) + ((e.fill_light || 0) * 0.5)) / 100.0;
    const contrast = 1.0 + (e.contrast || 0) / 100.0;
    const saturation = 1.0 + (e.saturation || 0) / 100.0;

    let filterStr = '';

    // Shadows / highlights curve and sharpening live in the inline SVG filter #pr-tone.
    const shadows = e.shadows || 0;
    const highlights = e.highlights || 0;
    const eff = e.effect;
    if (shadows !== 0 || highlights !== 0 || eff === 'sharpen') {
      const table = [];
      for (let i = 0; i <= 32; i++) table.push(this.toneCurve(i / 32, shadows, highlights).toFixed(4));
      const values = table.join(' ');
      document.querySelectorAll('#pr-tone feFuncR, #pr-tone feFuncG, #pr-tone feFuncB').forEach(f => f.setAttribute('tableValues', values));
      const a = eff === 'sharpen' ? 0.6 : 0;
      document.getElementById('pr-sharpen').setAttribute('kernelMatrix', `0 ${-a} 0 ${-a} ${1 + 4 * a} ${-a} 0 ${-a} 0`);
      filterStr += 'url(#pr-tone) ';
    }

    filterStr += `brightness(${Math.max(0.1, brightness)}) contrast(${Math.max(0.1, contrast)}) saturate(${Math.max(0, saturation)})`;

    if (eff === 'sepia') {
      filterStr += ' sepia(90%) hue-rotate(-20deg)';
    } else if (eff === 'bw') {
      filterStr += ' grayscale(100%) contrast(115%)';
    } else if (eff === 'warmify') {
      filterStr += ' sepia(25%) saturate(130%)';
    }

    const warmth = e.warmth || 0;
    if (warmth > 0) {
      filterStr += ` sepia(${Math.min(100, warmth * 0.3)}%)`;
    } else if (warmth < 0) {
      // Cooler: less red, more blue (ToneMath.Cool in C#).
      const w = Math.max(-100, warmth) * 0.002;
      document.getElementById('pr-cool-matrix').setAttribute('values', `${1 + w} 0 0 0 0  0 1 0 0 0  0 0 ${1 - w} 0 0  0 0 0 1 0`);
      filterStr += ' url(#pr-cool)';
    }

    img.style.filter = filterStr;

    // Straighten: rotate and zoom just enough that no empty corners show.
    const str = e.straighten || 0;
    if (str) {
      const w = img.naturalWidth || img.clientWidth || 1;
      const h = img.naturalHeight || img.clientHeight || 1;
      const a = Math.abs(str) * Math.PI / 180;
      const scale = Math.cos(a) + Math.max(w / h, h / w) * Math.sin(a);
      img.style.transform = `rotate(${str}deg) scale(${scale})`;
    } else {
      img.style.transform = 'none';
    }

    // Crop (hidden while the crop tool is open so the whole photo stays visible).
    const c = Array.isArray(e.crop) && e.crop.length === 4 && !this.isCropActive ? e.crop : null;
    if (c) {
      wrapper.style.clipPath = `inset(${c[1] * 100}% ${(1 - c[0] - c[2]) * 100}% ${(1 - c[1] - c[3]) * 100}% ${c[0] * 100}%)`;
    } else {
      wrapper.style.clipPath = str ? 'inset(0)' : 'none';
    }

    // Vignette: darken toward the corners of the (cropped) frame.
    const vig = e.vignette ?? (eff === 'vignette' ? 50 : 0);
    const strength = (eff === 'vignette' || vig > 0) && !this.isCropActive ? Math.min(100, Math.max(0, vig)) / 100 : 0;
    if (strength > 0) {
      const edgeAlpha = (1 - 0.75 * strength).toFixed(3);
      const gradient = `radial-gradient(ellipse farthest-corner at center, rgba(0,0,0,1) 35%, rgba(0,0,0,${edgeAlpha}) 100%)`;
      if (c) {
        const px = c[2] < 1 ? (c[0] / (1 - c[2])) * 100 : 0;
        const py = c[3] < 1 ? (c[1] / (1 - c[3])) * 100 : 0;
        setMask(gradient, `${c[2] * 100}% ${c[3] * 100}%`, `${px}% ${py}%`);
      } else {
        setMask(gradient, '100% 100%', '0 0');
      }
    } else {
      setMask('none', 'auto', '0 0');
    }
  },

  renderFilmstrip() {
    const track = document.getElementById('editor-filmstrip');
    const allPhotos = window.Picasa.state.photos;
    if (!allPhotos.length) {
      track.innerHTML = '';
      return;
    }

    // Filter filmstrip to current photo's folder (authentic Picasa behavior)
    let photos = allPhotos;
    if (this.currentPhoto && this.currentPhoto.folder_path) {
      const folderPhotos = allPhotos.filter(p => p.folder_path === this.currentPhoto.folder_path);
      if (folderPhotos.length > 0) photos = folderPhotos;
    }

    // Limit filmstrip thumbs to a window of max 60 photos around active photo
    const activeIdx = photos.findIndex(p => p.id === this.currentPhoto.id);
    let start = 0;
    let end = photos.length;
    if (photos.length > 60) {
      start = Math.max(0, activeIdx - 30);
      end = Math.min(photos.length, start + 60);
    }
    const visiblePhotos = photos.slice(start, end);

    const esc = window.Picasa.escapeHtml;
    track.innerHTML = visiblePhotos.map(p => `
      <div class="filmstrip-thumb ${p.id === this.currentPhoto.id ? 'active' : ''}" data-id="${p.id}" title="${esc(p.filename)}">
        <img src="/api/thumb/${p.id}" alt="${esc(p.filename)}" loading="lazy">
      </div>
    `).join('');

    track.querySelectorAll('.filmstrip-thumb').forEach(thumb => {
      thumb.addEventListener('click', () => {
        const id = parseInt(thumb.dataset.id, 10);
        const next = allPhotos.find(p => p.id === id);
        if (next) this.openPhoto(next);
      });
    });

    // Scroll active into view
    const activeThumb = track.querySelector('.filmstrip-thumb.active');
    if (activeThumb) {
      activeThumb.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
    }
  }
};
