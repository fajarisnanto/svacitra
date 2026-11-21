/**
 * svacitra - Main Application Controller
 * Handles global state, tabs, routing, keyboard shortcuts, and modals.
 */

window.Picasa = {
  state: {
    currentTab: 'library', // 'library', 'editor', 'collage'
    activeFolder: null,
    starredOnly: false,
    aiTaggedOnly: false,
    searchQuery: '',
    selectedPhotos: [], // array of photo objects currently in tray
    heldPhotos: [],     // array of photo objects held in tray
    isHeld: false,
    photos: [],         // full photo list currently loaded in gallery
    activePhotoIndex: -1, // active photo for viewer or editor
    scanStatus: null,
    aiSettings: {
      model: 'gpt-image-2.5-flare',
      endpoint: 'https://api.openai.com/v1',
      api_key: ''
    }
  },

  // Event bus for modules
  escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  },

  emit(event, data) {
    window.dispatchEvent(new CustomEvent(`picasa:${event}`, { detail: data }));
  },

  on(event, callback) {
    window.addEventListener(`picasa:${event}`, (e) => callback(e.detail));
  },

  init() {
    this.setupTabs();
    this.setupMenubar();
    this.setupExitHandlers();
    this.setupModals();
    this.setupAISettingsModal();
    this.setupKeyboardShortcuts();
    this.setupSearch();
    this.startScanPolling();

    // Init submodules
    if (window.PicasaLibrary) window.PicasaLibrary.init();
    if (window.PicasaTray) window.PicasaTray.init();
    if (window.PicasaViewer) window.PicasaViewer.init();
    if (window.PicasaEditor) window.PicasaEditor.init();
    if (window.PicasaCollage) window.PicasaCollage.init();
    if (window.PicasaSlideshow) window.PicasaSlideshow.init();
  },

  switchTab(tabName) {
    this.state.currentTab = tabName;

    document.querySelectorAll('.nav-tab').forEach(btn => {
      btn.classList.toggle("active", btn.id === `tab-${tabName}` || (tabName === "editor" && btn.id === "tab-edit"));
    });

    document.getElementById('view-library').style.display = tabName === 'library' ? 'flex' : 'none';
    document.getElementById('view-editor').style.display = tabName === 'editor' ? 'flex' : 'none';
    document.getElementById('view-collage').style.display = tabName === 'collage' ? 'flex' : 'none';

    this.emit('tabChanged', tabName);
  },

  setupTabs() {
    document.getElementById('tab-library').addEventListener('click', () => this.switchTab('library'));
    document.getElementById('tab-edit').addEventListener('click', () => {
      if (this.state.selectedPhotos.length > 0) {
        const photo = this.state.selectedPhotos[0];
        window.PicasaEditor.openPhoto(photo);
      } else if (this.state.photos.length > 0) {
        window.PicasaEditor.openPhoto(this.state.photos[0]);
      } else {
        this.switchTab('editor');
      }
    });
    document.getElementById('tab-collage').addEventListener('click', () => {
      this.switchTab('collage');
      if (window.PicasaCollage) window.PicasaCollage.renderPreview();
    });

    document.getElementById('btn-back-to-library').addEventListener('click', () => this.switchTab('library'));
  },

  setupSearch() {
    const searchInput = document.getElementById('global-search');
    const clearBtn = document.getElementById('clear-search');
    let debounceTimer;

    searchInput.addEventListener('input', (e) => {
      const q = e.target.value.trim();
      clearBtn.classList.toggle('show', q.length > 0);
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(() => {
        this.state.searchQuery = q;
        this.emit('searchChanged', q);
      }, 250);
    });

    clearBtn.addEventListener('click', () => {
      searchInput.value = '';
      clearBtn.classList.remove('show');
      this.state.searchQuery = '';
      this.emit('searchChanged', '');
    });
  },

  setupModals() {
    const modal = document.getElementById('modal-add-folder');
    const openBtn = document.getElementById('btn-import-folder');
    const quickAddBtn = document.getElementById('btn-quick-add-folder');
    const closeBtn = document.getElementById('btn-close-modal');
    const cancelBtn = document.getElementById('btn-cancel-modal');
    const confirmBtn = document.getElementById('btn-confirm-scan');
    const inputPath = document.getElementById('input-folder-path');
    const chipDefault = document.getElementById('chip-default-pictures');
    const chipSample = document.getElementById('chip-sample-folder');

    const openModal = () => {
      modal.style.display = 'flex';
      inputPath.focus();
    };
    const closeModal = () => {
      modal.style.display = 'none';
    };

    openBtn.addEventListener('click', openModal);
    quickAddBtn.addEventListener('click', openModal);
    closeBtn.addEventListener('click', closeModal);
    cancelBtn.addEventListener('click', closeModal);

    chipDefault.addEventListener('click', async () => {
      try {
        const res = await fetch('/api/default-folder');
        const data = await res.json();
        if (data.path) inputPath.value = data.path;
      } catch (e) {}
    });

    chipSample.addEventListener('click', () => {
      inputPath.value = 'sample_library';
    });

    confirmBtn.addEventListener('click', async () => {
      const folderPath = inputPath.value.trim();
      closeModal();
      try {
        const res = await fetch('/api/scan', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: folderPath || null })
        });
        const data = await res.json().catch(() => ({}));
        if (data.started === false) {
          alert(data.error || 'Could not scan that folder.');
          return;
        }
        this.pollScanStatusOnce();
      } catch (err) {
        console.error('Scan error:', err);
      }
    });
  },

  setupAISettingsModal() {
    const modal = document.getElementById('modal-ai-settings');
    const openBtn = document.getElementById('btn-open-ai-settings');
    const closeBtn = document.getElementById('btn-close-ai-modal');
    const cancelBtn = document.getElementById('btn-cancel-ai-modal');
    const saveBtn = document.getElementById('btn-save-ai-settings');
    const toggleKeyBtn = document.getElementById('btn-toggle-key-visibility');

    const modelInput = document.getElementById('input-ai-model');
    const endpointInput = document.getElementById('input-ai-endpoint');
    const keyInput = document.getElementById('input-ai-key');
    const statusText = document.getElementById('ai-status-text');

    // Preset buttons for models
    document.querySelectorAll('.model-preset-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const m = btn.getAttribute('data-model');
        const ep = btn.getAttribute('data-endpoint');
        const clearKey = btn.getAttribute('data-clear-key');
        if (m) modelInput.value = m;
        if (ep && ep !== 'local') endpointInput.value = ep;
        if (clearKey) keyInput.value = '';
      });
    });

    openBtn.addEventListener('click', async () => {
      modal.style.display = 'flex';
      try {
        const res = await fetch('/api/ai/settings');
        const data = await res.json();
        if (data.model) modelInput.value = data.model;
        if (data.endpoint) endpointInput.value = data.endpoint;
        keyInput.value = data.api_key || '';
        this.state.aiSettings = data;
        statusText.innerText = data.api_key 
          ? `Configured: ${data.model} at ${data.endpoint}` 
          : `Offline mode: Built-in Luna Smart Heuristic Engine (zero API key needed).`;
      } catch (e) {}
    });

    const closeModal = () => {
      keyInput.value = '';
      keyInput.type = 'password';
      modal.style.display = 'none';
    };

    closeBtn.addEventListener('click', closeModal);
    cancelBtn.addEventListener('click', closeModal);

    toggleKeyBtn.addEventListener('click', () => {
      keyInput.type = keyInput.type === 'password' ? 'text' : 'password';
    });

    saveBtn.addEventListener('click', async () => {
      const payload = {
        model: modelInput.value.trim() || 'gpt-image-2.5-flare',
        endpoint: endpointInput.value.trim() || 'https://api.openai.com/v1',
        api_key: keyInput.value.trim()
      };
      try {
        const res = await fetch('/api/ai/settings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        const data = await res.json();
        if (!res.ok || !data.success) throw new Error(data.error || 'Unable to save settings');
        this.state.aiSettings = data.ai_settings;
        alert(`Saved settings for ${payload.model}!`);
        closeModal();
      } catch (e) {
        alert('Failed to save AI settings: ' + e);
      }
    });
  },

  setupMenubar() {
    // File Menu
    document.getElementById('action-menu-add-folder')?.addEventListener('click', () => {
      document.getElementById('btn-import-folder')?.click();
    });
    document.getElementById('action-menu-export')?.addEventListener('click', () => {
      if (window.PicasaTray) window.PicasaTray.exportActive();
    });
    document.getElementById('action-menu-collage')?.addEventListener('click', () => {
      this.switchTab('collage');
      if (window.PicasaCollage) window.PicasaCollage.renderPreview();
    });
    document.getElementById('action-menu-exit')?.addEventListener('click', () => {
      this.exitApplication();
    });

    // Edit Menu
    document.getElementById('action-menu-lucky')?.addEventListener('click', () => {
      if (window.PicasaTray) window.PicasaTray.applyFeelingLuckyActive();
    });
    document.getElementById('action-menu-autotune')?.addEventListener('click', () => {
      if (this.state.currentTab !== 'editor') {
        const photo = this.state.selectedPhotos[0] || this.state.photos[0];
        if (photo && window.PicasaEditor) window.PicasaEditor.openPhoto(photo);
      }
      setTimeout(() => {
        document.getElementById('btn-tool-luna-autotune')?.click();
      }, 150);
    });
    document.getElementById('action-menu-rotate-left')?.addEventListener('click', () => {
      if (window.PicasaTray) window.PicasaTray.rotateActive(-90);
    });
    document.getElementById('action-menu-rotate-right')?.addEventListener('click', () => {
      if (window.PicasaTray) window.PicasaTray.rotateActive(90);
    });
    document.getElementById('action-menu-star')?.addEventListener('click', () => {
      if (window.PicasaTray) window.PicasaTray.toggleStarActive();
    });

    // View Menu
    document.getElementById('action-menu-view-library')?.addEventListener('click', () => {
      this.switchTab('library');
    });
    document.getElementById('action-menu-view-edit')?.addEventListener('click', () => {
      this.switchTab('editor');
    });
    document.getElementById('action-menu-view-collage')?.addEventListener('click', () => {
      this.switchTab('collage');
    });
    document.getElementById('action-menu-view-slideshow')?.addEventListener('click', () => {
      document.getElementById('btn-slideshow-all')?.click();
    });

    // Tools Menu
    document.getElementById('action-menu-tools-ai')?.addEventListener('click', () => {
      document.getElementById('btn-open-ai-settings')?.click();
    });
    document.getElementById('action-menu-tools-rescan')?.addEventListener('click', () => {
      document.getElementById('btn-refresh-library')?.click();
    });

    // Help Menu
    document.getElementById('action-menu-help-shortcuts')?.addEventListener('click', () => {
      alert(`svacitra Keyboard Shortcuts:\n\n` +
        `â€¢ Ctrl + 1 : Library View\n` +
        `â€¢ Ctrl + 2 : Edit Studio\n` +
        `â€¢ Ctrl + 3 : Collage Maker\n` +
        `â€¢ Ctrl + O : Add Folder to Library\n` +
        `â€¢ Ctrl + E : Export Active Photo\n` +
        `â€¢ Ctrl + I : Properties / EXIF information\n` +
        `â€¢ Ctrl + S : Star / Unstar Photo\n` +
        `â€¢ Ctrl + L : Rotate Left\n` +
        `â€¢ Ctrl + R : Rotate Right\n` +
        `â€¢ Ctrl + Q / Alt + F4 : Exit Application\n` +
        `â€¢ F5 / Space : Fullscreen Slideshow\n` +
        `â€¢ Double-click : Open photo in Edit Studio\n` +
        `â€¢ Esc : Back to Library\n` +
        `â€¢ F11 : Fullscreen window\n` +
        `â€¢ Mouse Wheel : Cursor-anchored Zoom (up to 10x)`);
    });
    document.getElementById('action-menu-help-about')?.addEventListener('click', () => {
      alert('svacitra\n\nCopyright © 2026 Fajar Isnanto — Jakarta, Indonesia.\nfajari@gmail.com\n\nDisclaimer\n\nsvacitra is provided as is, without warranty. Keep a separate backup of your original photos. Library backups contain edits and catalog information only. When you use cloud AI features, photos and prompts may be sent to your configured provider, and provider charges may apply.\n\nDistributed under the MIT License.');
    });
  },

  setupExitHandlers() {
    // Toolbar Exit Button
    document.getElementById('btn-app-exit')?.addEventListener('click', () => {
      this.exitApplication();
    });

    // Gracefully shut down backend server if window is closed by user (titlebar X)
    window.addEventListener('beforeunload', () => {
      navigator.sendBeacon('/api/shutdown');
    });
  },

  async exitApplication() {
    const confirmed = confirm('Are you sure you want to exit svacitra?');
    if (!confirmed) return;

    // Show nice closing screen
    document.body.innerHTML = `
      <div style="display:flex;flex-direction:column;align-items:center;justify-content:center;height:100vh;background:#18181c;color:#fff;font-family:Segoe UI, sans-serif;">
        <div style="font-size:3rem;margin-bottom:12px;">ðŸ“·</div>
        <h2 style="margin:0 0 8px 0;font-weight:600;font-size:1.4rem;">Exiting svacitra</h2>
        <p style="color:#888;margin:0;font-size:0.9rem;">Shutting down background services... Goodbye!</p>
      </div>
    `;

    try {
      await fetch('/api/shutdown', { method: 'POST' });
    } catch (e) {
      navigator.sendBeacon('/api/shutdown');
    }

    setTimeout(() => {
      window.close();
    }, 400);
  },

  setupKeyboardShortcuts() {
    window.addEventListener('keydown', (e) => {
      if (e.target.matches('input, textarea, select')) {
        if (e.key === 'Escape') e.target.blur();
        return;
      }

      // Exit application shortcut (Ctrl+Q or Alt+F4)
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'q') {
        e.preventDefault();
        this.exitApplication();
        return;
      }
      if (e.altKey && e.key === 'F4') {
        e.preventDefault();
        this.exitApplication();
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'o') {
        e.preventDefault();
        document.getElementById('btn-import-folder')?.click();
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'e') {
        e.preventDefault();
        if (window.PicasaTray) window.PicasaTray.exportActive();
        return;
      }

      // Viewer open?
      const viewer = document.getElementById('picasa-viewer');
      if (viewer && viewer.style.display !== 'none') {
        if (e.key === 'Escape') {
          window.PicasaViewer.close();
          return;
        }
        if (e.key === 'ArrowLeft') {
          window.PicasaViewer.prev();
          return;
        }
        if (e.key === 'ArrowRight') {
          window.PicasaViewer.next();
          return;
        }
        if (e.key === 'Enter') {
          const photo = window.PicasaViewer.getCurrentPhoto();
          if (photo) {
            window.PicasaViewer.close();
            window.PicasaEditor.openPhoto(photo);
          }
          return;
        }
      }

      // Slideshow open?
      const slideshow = document.getElementById('picasa-slideshow');
      if (slideshow && slideshow.style.display !== 'none') {
        if (e.key === 'Escape') {
          window.PicasaSlideshow.stop();
          return;
        }
        if (e.key === 'ArrowLeft') {
          window.PicasaSlideshow.prev();
          return;
        }
        if (e.key === 'ArrowRight') {
          window.PicasaSlideshow.next();
          return;
        }
        if (e.key === ' ' || e.key === 'Spacebar') {
          e.preventDefault();
          window.PicasaSlideshow.togglePlay();
          return;
        }
      }

      // App-wide shortcuts
      if (e.ctrlKey || e.metaKey) {
        if (e.key === '1') {
          e.preventDefault();
          this.switchTab('library');
        } else if (e.key === '2') {
          e.preventDefault();
          this.switchTab('editor');
        } else if (e.key === '3') {
          e.preventDefault();
          this.switchTab('collage');
        } else if (e.key.toLowerCase() === 's') {
          e.preventDefault();
          if (window.PicasaTray) window.PicasaTray.toggleStarActive();
        } else if (e.key.toLowerCase() === 'l') {
          e.preventDefault();
          if (window.PicasaTray) window.PicasaTray.rotateActive(-90);
        } else if (e.key.toLowerCase() === 'r') {
          e.preventDefault();
          if (window.PicasaTray) window.PicasaTray.rotateActive(90);
        }
      } else {
        if (e.key === 'Escape') {
          if (this.state.currentTab === 'editor') {
            this.switchTab('library');
          }
        }
      }
    });
  },

  startScanPolling() {
    setInterval(() => this.pollScanStatusOnce(), 2000);
  },

  async pollScanStatusOnce() {
    try {
      const res = await fetch('/api/scan/status');
      const status = await res.json();
      this.state.scanStatus = status;

      const footer = document.getElementById('scanner-footer');
      const label = document.getElementById('scan-label');
      const fill = document.getElementById('scan-progress-fill');

      if (status.is_scanning) {
        footer.style.display = 'flex';
        const total = status.total_found || 1;
        const count = status.processed_count || 0;
        const pct = Math.min(100, Math.round((count / total) * 100));
        label.innerText = `Scanning: ${count}/${total} photos`;
        fill.style.width = `${pct}%`;
      } else {
        footer.style.display = 'none';
      }

      // Keep the library in sync with the backend: the gallery reloads when a scan finishes or the
      // folder watcher adds/removes photos, and the sidebar counts tick up while a scan is running.
      const version = status.library_version;
      const changed = this._libraryVersion !== undefined && version !== this._libraryVersion;
      const scanEnded = this._wasScanning && !status.is_scanning;
      this._libraryVersion = version;
      this._wasScanning = !!status.is_scanning;
      if (window.PicasaLibrary) {
        if (scanEnded || (changed && !status.is_scanning)) {
          window.PicasaLibrary.refresh();
        } else if (changed) {
          window.PicasaLibrary.loadSummary().catch(() => {});
          if (!this.state.photos.length) window.PicasaLibrary.loadPhotos();
        }
      }
      if (status.is_scanning) {
        clearTimeout(this._fastPoll);
        this._fastPoll = setTimeout(() => this.pollScanStatusOnce(), 700);
      }
    } catch (e) {}
  }
};

document.addEventListener('DOMContentLoaded', () => {
  window.Picasa.init();
});
