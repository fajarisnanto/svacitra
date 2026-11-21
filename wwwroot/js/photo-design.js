window.PhotoDesign = {
  defaults: { title: '', art: '', style: 'outline', font: 'sans', size: 'medium',
    position: 'center', color: '#ffffff', frame: 'none', stamp: 'none' },

  init(editor) {
    this.editor = editor;
    this.controls = [...document.querySelectorAll('[data-design]')];
    this.image = document.getElementById('design-preview');
    this.note = document.getElementById('design-render-note');
    for (const control of this.controls) control.addEventListener('input', () => {
      if (!editor.currentPhoto) return;
      const design = Object.fromEntries(this.controls.map(el => [el.dataset.design, el.value]));
      if (design.title.trim() || design.art.trim() || design.frame !== 'none' || design.stamp !== 'none')
        editor.edits.design = design;
      else delete editor.edits.design;
      this.updateStamp(design.stamp);
      editor.applyLivePreview();
    });
    document.getElementById('clear-design').addEventListener('click', () => {
      delete editor.edits.design;
      this.sync();
      editor.applyLivePreview();
    });
  },

  sync() {
    const values = { ...this.defaults, ...this.editor.edits.design };
    for (const el of this.controls) el.value = values[el.dataset.design];
    this.updateStamp(values.stamp);
  },

  async updateStamp(format) {
    const photo = this.editor.currentPhoto;
    const note = document.getElementById('design-date-note');
    if (!photo || !format || format === 'none') { note.textContent = ''; return; }
    if (this.dateId !== photo.id) {
      this.dateId = photo.id;
      this.dateRequest = fetch('/api/photo/' + photo.id + '/capture-time').then(r => {
        if (!r.ok) throw new Error();
        return r.json();
      });
    }
    try {
      const data = await this.dateRequest;
      if (photo !== this.editor.currentPhoto || this.editor.edits.design?.stamp !== format) return;
      note.textContent = data.captured ? (format === 'date' ? data.captured.slice(0,10) : data.captured)
        : 'No EXIF capture time in this photo. The timestamp will be omitted.';
    } catch {
      if (photo === this.editor.currentPhoto && this.editor.edits.design?.stamp === format) {
        note.textContent = 'Could not read capture time.';
        this.dateId = null;
      }
    }
  },

  reset() {
    clearTimeout(this.timer);
    this.controller?.abort();
    this.key = null;
    this.image.hidden = true;
    this.image.removeAttribute('src');
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = null;
    this.note.textContent = '';
  },

  preview(show) {
    const photo = this.editor.currentPhoto;
    if (!show || !photo || !this.editor.edits.design) {
      if (this.key) this.reset();
      return;
    }
    const body = JSON.stringify({ edits: this.editor.edits });
    const key = photo.id + ':' + body;
    if (key === this.key) return;
    this.key = key;
    clearTimeout(this.timer);
    this.controller?.abort();
    const controller = this.controller = new AbortController();
    this.timer = setTimeout(async () => {
      let url;
      try {
        this.note.textContent = 'Rendering design…';
        const res = await fetch('/api/photo/' + photo.id + '/prompt-preview', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body, signal: controller.signal
        });
        if (!res.ok) throw new Error();
        url = URL.createObjectURL(await res.blob());
        const ready = new Image();
        ready.src = url;
        await ready.decode();
        if (controller.signal.aborted) return;
        this.image.src = url;
        this.image.hidden = false;
        if (this.url) URL.revokeObjectURL(this.url);
        this.url = url;
        url = null;
        this.note.textContent = '';
      } catch {
        if (!controller.signal.aborted) {
          this.image.hidden = true;
          this.note.textContent = 'Design preview unavailable. Check that the original photo is online.';
          this.key = null;
        }
      } finally { if (url) URL.revokeObjectURL(url); }
    }, 220);
  }
};
