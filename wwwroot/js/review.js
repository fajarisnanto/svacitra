// Selection-based editing and client proof sheets.
window.PhotoReview = {
  fields: {
    brightness: 'Brightness', contrast: 'Contrast', warmth: 'Warmth', saturation: 'Saturation',
    fill_light: 'Fill light', highlights: 'Highlights', shadows: 'Shadows', effect: 'Effect',
    vignette: 'Vignette strength', clarity: 'Clarity', detail: 'Detail', denoise: 'Noise reduction',
    redeye: 'Red-eye correction', feeling_lucky: 'Auto-enhance'
  },

  init() {
    const el = id => document.getElementById(id);
    this.status = el('review-status');
    this.paste = el('paste-edits');
    this.undo = el('undo-batch');
    this.export = el('export-proofs');
    this.compare = el('compare-picks');
    window.ComparePicks.init();
    this.compare.onclick = () => window.ComparePicks.open(window.Picasa.state.selectedPhotos);
    this.refreshUndo();
    this.undo.onclick = () => this.run(async () => {
      if (this.busy) return 'A batch edit is already saving.';
      this.busy = true;
      this.undo.disabled = true;
      try {
        const result = await this.post('/api/photos/batch-undo', { token: this.token });
        this.syncRecipes(result, []);
        return 'Restored ' + result.updated.length + ' photos.' +
          (result.skipped ? ' Skipped ' + result.skipped + ' changed or removed since the batch.' : '');
      } catch (err) {
        await this.refreshUndo();
        throw err;
      } finally {
        this.busy = false;
        this.undo.disabled = false;
        this.selectionChanged(window.Picasa.state.selectedPhotos.length);
      }
    });
    this.paste.onclick = () => this.run(async () => {
      const count = await this.apply([...window.Picasa.state.selectedPhotos], this.clipboard);
      return 'Pasted selected edits onto ' + count + ' photos.';
    });
    el('copy-edit-panel').ontoggle = e => {
      if (!e.target.open) return;
      const edits = window.PicasaEditor.edits || {};
      el('copy-status').textContent = '';
      el('copy-fields').replaceChildren(...Object.entries(this.fields).map(([key, text]) => {
        const label = document.createElement('label');
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.value = key;
        input.checked = Object.hasOwn(edits, key);
        label.append(input, text);
        return label;
      }));
    };
    el('copy-edit-values').onclick = () => {
      const edits = window.PicasaEditor.edits;
      const patch = {};
      for (const field of el('copy-fields').querySelectorAll('input:checked')) {
        const key = field.value;
        if (key === 'effect') patch[key] = edits[key] || 'none';
        else if (key === 'redeye' || key === 'feeling_lucky') patch[key] = Boolean(edits[key]);
        else patch[key] = Number(edits[key] ?? (key === 'vignette' && edits.effect === 'vignette' ? 50 : 0));
      }
      if (!Object.keys(patch).length) { el('copy-status').textContent = 'Choose at least one setting.'; return; }
      this.clipboard = patch;
      this.selectionChanged(window.Picasa.state.selectedPhotos.length);
      this.status.textContent = 'Select destination photos, then choose Paste edits.';
      window.Picasa.switchTab('library');
    };

    this.export.onclick = () => this.run(async () => {
      const photo_ids = window.Picasa.state.selectedPhotos.map(p => p.id);
      this.status.textContent = 'Preparing proof sheet…';
      this.exporting = true;
      this.selectionChanged(photo_ids.length);
      try {
        const result = await this.post('/api/proofs/export', { photo_ids });
        if (result.cancelled) return 'Export cancelled.';
        el('proof-code').value = result.code;
        return 'Saved ' + result.count + ' proofs to ' + result.path + ' · Sheet ' + result.code;
      } finally {
        this.exporting = false;
        this.selectionChanged(window.Picasa.state.selectedPhotos.length);
      }
    });
    const form = el('proof-lookup');
    el('find-proofs').onclick = () => {
      form.hidden = !form.hidden;
      el('find-proofs').setAttribute('aria-expanded', String(!form.hidden));
      if (!form.hidden) el('proof-code').focus();
    };
    form.onsubmit = e => {
      e.preventDefault();
      this.run(async () => {
        const parts = el('proof-numbers').value.trim().split(/[\s,;]+/);
        if (!parts.every(p => /^\d+$/.test(p))) throw new Error('Enter numbers separated by commas or spaces.');
        const data = await this.post('/api/proofs/resolve', { code: el('proof-code').value, numbers: parts.map(Number) });
        if (data.photos.length) {
          window.PicasaLibrary.rangeAnchor = null;
          window.PicasaTray.selectRange(data.photos, false);
        }
        return 'Found ' + data.photos.length + ' originals.' + (data.missing.length ? ' Missing numbers: ' + data.missing.join(', ') : '');
      });
    };
  },

  selectionChanged(count) {
    this.compare.disabled = count < 2;
    this.export.disabled = !count || !!this.exporting;
    this.paste.hidden = !this.clipboard;
    this.paste.disabled = !count || !!this.busy;
    this.paste.textContent = 'Paste edits (' + count + ')';
  },

  async run(action) {
    try { this.status.textContent = await action(); }
    catch (err) { this.status.textContent = err.message; }
  },

  async post(url, body) {
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || 'Request failed.');
    return result;
  },

  async refreshUndo() {
    const sequence = this.undoSequence;
    try {
      const res = await fetch('/api/photos/batch-undo');
      if (!res.ok) return;
      const data = await res.json();
      if (sequence === this.undoSequence) this.setUndo(data.undo_token);
    } catch { /* The library may still be opening. A successful batch also sets the token. */ }
  },

  setUndo(token) {
    this.undoSequence = (this.undoSequence || 0) + 1;
    this.token = token;
    this.undo.hidden = !token;
  },

  async apply(photos, edits) {
    if (this.busy) throw new Error('A batch edit is already saving.');
    this.busy = true;
    this.undo.disabled = true;
    try {
      const data = await this.post('/api/photos/batch-edit', { photo_ids: photos.map(p => p.id), edits });
      if (data.updated.length) this.syncRecipes(data, photos);
      return data.updated.length;
    } finally {
      this.busy = false;
      this.undo.disabled = false;
      this.selectionChanged(window.Picasa.state.selectedPhotos.length);
    }
  },

  syncRecipes(data, targets) {
    this.setUndo(data.undo_token);
    const library = window.PicasaLibrary;
    const changed = new Map(data.updated.map(p => [p.id, p.edits]));
    const version = Date.now();
    for (const id of changed.keys()) library.thumbVersions.set(id, version);
    for (const p of [...targets, ...window.Picasa.state.photos, ...window.Picasa.state.selectedPhotos]) {
      if (changed.has(p.id)) p.edits = changed.get(p.id);
    }
    for (const card of document.querySelectorAll('.photo-card')) {
      const id = Number(card.dataset.id);
      if (changed.has(id)) card.querySelector('img').src = library.thumbUrl(id);
    }
    window.PicasaTray.updateTrayUI();
  }
};
