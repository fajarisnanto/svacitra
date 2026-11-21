/**
 * svacitra - Native bridge
 * Glue between the WPF shell (native menu bar, folder pickers) and the web frontend, plus features the
 * C# backend adds: multi-photo export, background batch AI tagging, rescan, folder removal,
 * a draggable crop box, and an AI connection test.
 * Loaded last, after all other modules.
 */

window.PicasaNative = {
  isNative: !!window.__PICASA_NATIVE__,
  batchTimer: null,

  async postJson(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
    let data;
    try {
      data = await res.json();
    } catch (e) {
      data = { success: false, error: `Unexpected response (HTTP ${res.status})` };
    }
    return data;
  },

  inEditor() {
    return !window.ComparePicks?.active && window.Picasa.state.currentTab === 'editor' && window.PicasaEditor && window.PicasaEditor.currentPhoto;
  },

  // ---------------------------------------------------------------- folders

  async addFolder(path) {
    const data = await this.postJson('/api/scan', { path: path || null });
    if (!data.started) {
      alert(data.error || 'Could not scan that folder.');
      return false;
    }
    window.Picasa.pollScanStatusOnce();
    return true;
  },

  async browseFolder() {
    const data = await this.postJson('/api/pick-folder', {});
    return data && data.path ? data.path : null;
  },

  async rescan() {
    await this.postJson('/api/rescan', {});
    window.Picasa.pollScanStatusOnce();
  },

  async removeActiveFolder() {
    const path = window.PicasaLibrary.activeFolderPath;
    if (!path) {
      alert('Select a folder in the "Folders on Disk" list first.');
      return;
    }
    await this.removeFolder(path);
  },

  async removeFolder(path) {
    if (!confirm(`Remove this folder from the svacitra library?\n\n${path}\n\nThe photos stay on disk; only the library entries (stars, edits, AI tags) are removed.`)) return;
    const data = await this.postJson('/api/folders/remove', { path });
    if (data.error) {
      alert(data.error);
      return;
    }
    window.PicasaLibrary.activeFolderPath = null;
    window.PicasaLibrary.activeFilter = 'all';
    window.Picasa.state.activeFolder = null;
    window.Picasa.state.selectedPhotos = [];
    window.PicasaTray.updateTrayUI();
    document.querySelectorAll('.sidebar-item').forEach(i => i.classList.toggle('active', i.dataset.filter === 'all'));
    window.PicasaLibrary.refresh();
  },

  // ---------------------------------------------------------------- export

  async exportSelected(destDir) {
    let photos = window.Picasa.state.selectedPhotos;
    if (this.inEditor()) {
      // In Edit Studio, export always means the photo on screen, exactly as it looks now. The tray
      // selection can still hold the photo that was picked in the library (or the original of an AI copy).
      const editor = window.PicasaEditor;
      const current = editor.currentPhoto;
      const edits = JSON.parse(JSON.stringify(editor.edits || {}));
      const saved = await this.postJson(`/api/photo/${current.id}/save-edit`, { edits }).catch(e => ({ error: String(e) }));
      if (saved && saved.error) {
        alert('Export failed: could not save the current edits (' + saved.error + ')');
        return;
      }
      current.edits = edits;
      photos = [current];
    }
    if (!photos.length) {
      alert('Please select photo(s) to export.');
      return;
    }
    try {
      const data = await this.postJson('/api/export', {
        photo_ids: photos.map(p => p.id),
        dest_dir: destDir || null
      });
      if (data.exported) {
        const extra = data.errors && data.errors.length ? `\n\nSkipped:\n${data.errors.join('\n')}` : '';
        alert(data.count > 1
          ? `Exported ${data.count} photos to:\n${data.dest_dir}${extra}`
          : `Exported photo copy to:\n${data.path}${extra}`);
      } else {
        alert('Export failed: ' + (data.error || 'unknown error'));
      }
    } catch (e) {
      alert('Export failed: ' + e);
    }
  },

  // ---------------------------------------------------------------- properties / EXIF

  /** The photo the user is looking at: Edit Studio photo, viewer photo, or the last selected one. */
  focusedPhoto() {
    if (this.inEditor()) return window.PicasaEditor.currentPhoto;
    const viewer = document.getElementById('picasa-viewer');
    if (viewer && viewer.style.display !== 'none' && window.PicasaViewer && window.PicasaViewer.getCurrentPhoto) {
      const p = window.PicasaViewer.getCurrentPhoto();
      if (p) return p;
    }
    const sel = window.Picasa.state.selectedPhotos;
    return sel.length ? sel[sel.length - 1] : null;
  },

  async showProperties() {
    const photo = this.focusedPhoto();
    if (!photo) {
      alert('Please select a photo first.');
      return;
    }
    const modal = document.getElementById('modal-photo-info');
    const body = document.getElementById('photo-info-body');
    document.getElementById('photo-info-title').textContent = 'Properties: ' + photo.filename;
    body.textContent = 'Reading metadata...';
    modal.style.display = 'flex';
    this.infoText = '';
    try {
      const res = await fetch(`/api/photo/${photo.id}/exif`);
      const data = await res.json();
      if (data.error) throw new Error(data.error);
      body.textContent = '';
      const lines = [data.file_path];
      const addSection = (parent, s) => {
        const h = document.createElement('div');
        h.className = 'photo-info-section';
        h.textContent = s.title.replace(/^All: /, '');
        parent.appendChild(h);
        const table = document.createElement('table');
        table.className = 'photo-info-table';
        lines.push('', '[' + s.title + ']');
        for (const it of s.items) {
          const tr = table.insertRow();
          tr.insertCell().textContent = it.label;
          tr.insertCell().textContent = it.value;
          lines.push(`${it.label}: ${it.value}`);
        }
        parent.appendChild(table);
      };
      const main = data.sections.filter(s => !s.title.startsWith('All: '));
      const all = data.sections.filter(s => s.title.startsWith('All: '));
      main.forEach(s => addSection(body, s));
      if (!data.has_exif) {
        const note = document.createElement('div');
        note.className = 'photo-info-note';
        note.textContent = 'This file has no EXIF information.';
        body.appendChild(note);
      }
      if (all.length) {
        const details = document.createElement('details');
        details.className = 'photo-info-all';
        const summary = document.createElement('summary');
        summary.textContent = 'All metadata';
        details.appendChild(summary);
        all.forEach(s => addSection(details, s));
        body.appendChild(details);
      }
      this.infoText = lines.join('\n');
    } catch (e) {
      body.textContent = 'Could not read the metadata: ' + (e.message || e);
    }
  },

  closeProperties() {
    document.getElementById('modal-photo-info').style.display = 'none';
  },

  // ---------------------------------------------------------------- edit menu

  ensureEditorPhotoSelected() {
    if (this.inEditor()) {
      const p = window.PicasaEditor.currentPhoto;
      if (!window.Picasa.state.selectedPhotos.some(s => s.id === p.id)) window.PicasaTray.selectSingle(p);
    }
  },

  toggleStar() {
    this.ensureEditorPhotoSelected();
    window.PicasaTray.toggleStarActive();
  },

  rotate(delta) {
    if (this.inEditor()) {
      document.getElementById(delta < 0 ? 'btn-rotate-edit-left' : 'btn-rotate-edit-right').click();
    } else {
      window.PicasaTray.rotateActive(delta);
    }
  },

  undoAllFixes() {
    if (this.inEditor()) document.getElementById('btn-undo-all').click();
    else alert('Open a photo in Edit Studio first (double-click it).');
  },

  saveEdits() {
    if (this.inEditor()) document.getElementById('btn-save-edits').click();
    else alert('Open a photo in Edit Studio first (double-click it).');
  },

  feelingLucky() {
    if (this.inEditor()) document.getElementById('btn-tool-lucky').click();
    else window.PicasaTray.applyFeelingLuckyActive();
  },

  // ---------------------------------------------------------------- batch AI tagging

  async batchTagAll() {
    const summary = await (await fetch('/api/summary')).json();
    const total = summary.total_photos || 0;
    const untagged = total - (summary.ai_tagged_count || 0);
    if (!total) {
      alert('Your library is empty. Add a folder first.');
      return;
    }
    const onlyUntagged = untagged > 0;
    const count = onlyUntagged ? untagged : total;
    const engine = summary.ai_settings && summary.ai_settings.api_key
      ? `${summary.ai_settings.model} (falls back to the offline engine if the cloud call fails)`
      : 'the offline Luna Smart Heuristic Engine';
    if (!confirm(`Tag ${count} ${onlyUntagged ? 'untagged ' : ''}photo(s) with ${engine}?\n\nThis runs in the background; you can keep working.`)) return;

    const data = await this.postJson('/api/ai/batch-analyze', { all: true, only_untagged: onlyUntagged });
    if (!data.started) {
      alert(data.error || 'Could not start batch tagging.');
      return;
    }
    this.watchBatch(true);
  },

  batchFooter() {
    let el = document.getElementById('luna-batch-footer');
    if (!el) {
      el = document.createElement('div');
      el.id = 'luna-batch-footer';
      el.className = 'sidebar-footer';
      el.style.marginTop = '0';
      el.innerHTML = `
        <div class="scan-spinner"></div>
        <div class="scan-text">
          <div class="scan-label" id="luna-batch-label">Luna tagging...</div>
          <div class="scan-progress-bar"><div class="scan-bar-fill" id="luna-batch-fill" style="background:#9c27b0;"></div></div>
        </div>`;
      document.querySelector('.picasa-sidebar').appendChild(el);
    }
    return el;
  },

  watchBatch(announce) {
    clearInterval(this.batchTimer);
    const footer = this.batchFooter();
    let lastDone = -1;
    const tick = async () => {
      let st;
      try {
        st = await (await fetch('/api/ai/batch-status')).json();
      } catch (e) {
        return;
      }
      if (st.running) {
        footer.style.display = 'flex';
        document.getElementById('luna-batch-label').innerText = `Luna tagging: ${st.done}/${st.total} photos`;
        document.getElementById('luna-batch-fill').style.width = `${st.total ? Math.round(st.done / st.total * 100) : 0}%`;
        if (st.done !== lastDone) {
          lastDone = st.done;
          window.PicasaLibrary.loadSummary();
        }
        return;
      }
      clearInterval(this.batchTimer);
      this.batchTimer = null;
      footer.style.display = 'none';
      if (lastDone >= 0 || announce) {
        window.PicasaLibrary.refresh();
        if (announce) {
          let msg = `Luna AI finished tagging ${st.done} photo(s).`;
          if (st.fallbacks > 0 && st.last_error) {
            msg += `\n\nNotice: the cloud API returned:\n"${st.last_error}"\n\n${st.fallbacks} photo(s) were tagged with the offline Smart Heuristic engine instead.`;
          }
          if (st.failed > 0) msg += `\n\n${st.failed} photo(s) could not be analyzed.`;
          alert(msg);
        }
      }
    };
    this.batchTimer = setInterval(tick, 1000);
    tick();
  },

  // ---------------------------------------------------------------- UI wiring

  init() {
    // Multi-photo export replaces the single-photo one.
    window.PicasaTray.exportActive = () => this.exportSelected(null);

    // Properties / EXIF dialog.
    const infoModal = document.getElementById('modal-photo-info');
    document.getElementById('btn-action-info')?.addEventListener('click', () => this.showProperties());
    document.getElementById('viewer-btn-info')?.addEventListener('click', () => this.showProperties());
    document.getElementById('btn-close-photo-info')?.addEventListener('click', () => this.closeProperties());
    document.getElementById('btn-photo-info-ok')?.addEventListener('click', () => this.closeProperties());
    document.getElementById('btn-photo-info-copy')?.addEventListener('click', async (e) => {
      try {
        await navigator.clipboard.writeText(this.infoText || '');
        e.currentTarget.textContent = 'Copied';
      } catch (err) {
        e.currentTarget.textContent = 'Copy failed';
      }
      const btn = e.currentTarget;
      setTimeout(() => { btn.textContent = 'Copy'; }, 1500);
    });
    infoModal?.addEventListener('click', (e) => { if (e.target === infoModal) this.closeProperties(); });
    window.addEventListener('keydown', (e) => {
      const open = infoModal && infoModal.style.display !== 'none';
      if (open && e.key === 'Escape') {
        e.stopImmediatePropagation();
        e.preventDefault();
        this.closeProperties();
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'i' && !['INPUT', 'TEXTAREA'].includes(e.target.tagName)) {
        e.preventDefault();
        if (open) this.closeProperties(); else this.showProperties();
      }
    }, true);

    // "Browse..." in the Add Folder dialog opens the native Windows folder picker.
    const browseBtn = document.getElementById('btn-browse-folder');
    if (browseBtn) {
      browseBtn.addEventListener('click', async () => {
        const path = await this.browseFolder();
        if (path) document.getElementById('input-folder-path').value = path;
      });
      fetch('/api/app-info').then(r => r.json()).then(info => {
        if (!info.native_folder_picker) browseBtn.style.display = 'none';
      }).catch(() => {});
    }
    const pathInput = document.getElementById('input-folder-path');
    if (pathInput) {
      pathInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') document.getElementById('btn-confirm-scan').click();
      });
    }

    // "Test Connection" in the Luna AI dialog.
    const testBtn = document.getElementById('btn-test-ai-settings');
    if (testBtn) {
      testBtn.addEventListener('click', async () => {
        const statusText = document.getElementById('ai-status-text');
        const dot = document.querySelector('#ai-status-banner .status-dot');
        statusText.innerText = 'Testing connection...';
        testBtn.disabled = true;
        try {
          const data = await this.postJson('/api/ai/test', {
            model: document.getElementById('input-ai-model').value.trim(),
            endpoint: document.getElementById('input-ai-endpoint').value.trim(),
            api_key: document.getElementById('input-ai-key').value.trim()
          });
          statusText.innerText = data.success ? data.message : `Failed: ${data.error}`;
          if (dot) dot.style.background = data.success ? '#43a047' : '#e53935';
        } catch (e) {
          statusText.innerText = 'Failed: ' + e;
        } finally {
          testBtn.disabled = false;
        }
      });
    }

    // Right-click a folder in the sidebar to remove it from the library.
    const folderList = document.getElementById('folders-tree-list');
    if (folderList) {
      folderList.addEventListener('contextmenu', (e) => {
        const item = e.target.closest('.folder-tree-item');
        if (!item) return;
        e.preventDefault();
        this.removeFolder(decodeURIComponent(item.dataset.path));
      });
    }

    // Rescan button: really rescan the watched folders instead of only reloading the list.
    const refreshBtn = document.getElementById('btn-refresh-library');
    if (refreshBtn) refreshBtn.addEventListener('click', () => this.rescan());

    this.setupCropDrag();

    // Resume the progress display if a batch is still running (e.g. after a page reload).
    fetch('/api/ai/batch-status').then(r => r.json()).then(st => {
      if (st.running) this.watchBatch(false);
    }).catch(() => {});
  },

  /** Makes the crop box movable (drag inside) and resizable (drag a corner handle). */
  setupCropDrag() {
    const overlay = document.getElementById('crop-overlay');
    if (!overlay) return;
    const MIN = 0.05;
    let drag = null;

    overlay.addEventListener('mousedown', (e) => {
      const wrapper = overlay.offsetParent || overlay.parentElement;
      const box = wrapper.getBoundingClientRect();
      if (!box.width || !box.height) return;
      drag = {
        handle: e.target.dataset ? e.target.dataset.handle : null,
        startX: e.clientX,
        startY: e.clientY,
        box,
        rect: { ...window.PicasaEditor.cropRect }
      };
      document.querySelectorAll('.aspect-pills .pill-btn').forEach(b => b.classList.toggle('active', b.dataset.ratio === 'manual' || b.dataset.ratio === 'free'));
      e.preventDefault();
      e.stopPropagation();
    });

    window.addEventListener('mousemove', (e) => {
      if (!drag) return;
      const dx = (e.clientX - drag.startX) / drag.box.width;
      const dy = (e.clientY - drag.startY) / drag.box.height;
      const r = { ...drag.rect };
      const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

      if (!drag.handle) {
        r.x = clamp(drag.rect.x + dx, 0, 1 - r.w);
        r.y = clamp(drag.rect.y + dy, 0, 1 - r.h);
      } else {
        let left = drag.rect.x, top = drag.rect.y, right = drag.rect.x + drag.rect.w, bottom = drag.rect.y + drag.rect.h;
        if (drag.handle.includes('w')) left = clamp(left + dx, 0, right - MIN);
        if (drag.handle.includes('e')) right = clamp(right + dx, left + MIN, 1);
        if (drag.handle.includes('n')) top = clamp(top + dy, 0, bottom - MIN);
        if (drag.handle.includes('s')) bottom = clamp(bottom + dy, top + MIN, 1);
        r.x = left; r.y = top; r.w = right - left; r.h = bottom - top;
      }
      window.PicasaEditor.cropRect = r;
      window.PicasaEditor.updateCropOverlayStyle();
    });

    window.addEventListener('mouseup', () => { drag = null; });
  }
};

document.addEventListener('DOMContentLoaded', () => {
  window.PicasaNative.init();
});
