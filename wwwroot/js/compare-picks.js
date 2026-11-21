window.ComparePicks = {
  init() {
    const el = id => document.getElementById(id);
    this.panel = el('pick-workspace');
    this.left = el('pick-left');
    this.right = el('pick-right');
    this.zoomControl = el('pick-zoom');
    this.next = el('pick-next');
    this.promote = el('pick-promote');
    this.star = el('pick-star');
    this.status = el('pick-status');
    this.next.onclick = () => this.advance(false);
    this.promote.onclick = () => this.advance(true);
    this.star.onclick = () => this.markFavorite();
    el('pick-done').onclick = () => window.Picasa.switchTab('library');
    this.zoomControl.onchange = () => this.setZoom(Number(this.zoomControl.value));
    for (const pane of [this.left, this.right]) this.setupPan(pane);
    window.Picasa.on('tabChanged', tab => { if (tab !== 'editor') this.close(); });
    window.addEventListener('keydown', e => {
      if (!this.active || e.target.matches('input,select,textarea')) return;
      if (e.key === 'ArrowRight') this.advance(false);
      else if (e.key === 'Escape') window.Picasa.switchTab('library');
      else return;
      e.preventDefault();
      e.stopImmediatePropagation();
    }, true);
  },

  open(selection) {
    if (selection.length < 2) return;
    this.photos = [...selection];
    this.images = new Map();
    this.index = 1;
    this.favorite = this.photos[0];
    this.x = this.y = 0.5;
    this.active = true;
    window.Picasa.switchTab('editor');
    document.getElementById('view-editor').classList.add('picking');
    this.panel.hidden = false;
    this.setZoom(1);
    this.render();
    this.next.focus();
  },

  close() {
    if (!this.active) return;
    this.active = false;
    this.panel.hidden = true;
    document.getElementById('view-editor').classList.remove('picking');
    this.left.replaceChildren();
    this.right.replaceChildren();
    this.images.clear();
    this.photos = [];
    this.drag = null;
  },

  advance(promote) {
    if (this.index >= this.photos.length) return;
    if (promote) this.favorite = this.photos[this.index];
    this.index++;
    this.render();
  },

  image(photo) {
    let img = this.images.get(photo.id);
    if (img) return img;
    img = new Image();
    img.alt = photo.filename;
    img.draggable = false;
    img.onerror = () => { img.alt = 'Preview unavailable · ' + photo.filename; };
    const version = window.PicasaLibrary.thumbVersions.get(photo.id) || 0;
    img.src = '/api/image/' + photo.id + '?max_dim=1920&t=' + version;
    this.images.set(photo.id, img);
    return img;
  },

  render() {
    const challenger = this.photos[this.index];
    const el = id => document.getElementById(id);
    el('pick-left-label').textContent = 'Favorite · ' + this.favorite.filename;
    el('pick-right-label').textContent = challenger ? 'Challenger · ' + challenger.filename : 'Review complete';
    this.left.replaceChildren(this.image(this.favorite));
    this.right.replaceChildren(...(challenger ? [this.image(challenger)] : []));
    this.next.disabled = this.promote.disabled = !challenger;
    this.star.disabled = !!this.favorite.is_starred;
    this.star.textContent = this.favorite.is_starred ? 'Favorite starred' : 'Star favorite';
    this.status.textContent = challenger ? (this.index + 1) + ' of ' + this.photos.length : 'All photos reviewed.';
    const keep = new Set([this.favorite.id]);
    for (const p of this.photos.slice(this.index, this.index + 3)) { keep.add(p.id); this.image(p); }
    for (const key of this.images.keys()) if (!keep.has(key)) this.images.delete(key);
    this.transform();
  },

  async markFavorite() {
    const p = this.favorite;
    this.star.disabled = true;
    try {
      await window.PhotoReview.post('/api/photo/' + p.id + '/pick', {});
      p.is_starred = 1;
      window.Picasa.emit('photoUpdated', p);
      window.PicasaLibrary.loadSummary().catch(() => {});
      if (this.active && this.favorite === p) this.render();
    } catch (err) {
      if (this.active && this.favorite === p) { this.status.textContent = err.message; this.star.disabled = false; }
    }
  },

  setZoom(zoom) {
    this.zoom = zoom;
    this.zoomControl.value = String(zoom);
    this.panel.classList.toggle('zoomed', zoom > 1);
    this.transform();
  },

  transform() {
    for (const pane of [this.left, this.right]) {
      const img = pane.firstElementChild;
      if (!img) continue;
      img.style.transformOrigin = (this.x * 100) + '% ' + (this.y * 100) + '%';
      img.style.transform = 'scale(' + this.zoom + ')';
    }
  },

  setupPan(pane) {
    const clamp = value => Math.max(0, Math.min(1, value));
    pane.onpointerdown = e => {
      if (e.button !== 0 || e.target.tagName !== 'IMG') return;
      const rect = e.target.getBoundingClientRect();
      this.drag = { id: e.pointerId, startX: e.clientX, startY: e.clientY, x: this.x, y: this.y,
        focusX: (e.clientX - rect.left) / rect.width, focusY: (e.clientY - rect.top) / rect.height };
      pane.setPointerCapture(e.pointerId);
    };
    pane.onpointermove = e => {
      const d = this.drag;
      const img = pane.firstElementChild;
      if (!d || d.id !== e.pointerId || this.zoom <= 1 || !img) return;
      this.x = clamp(d.x - (e.clientX - d.startX) / (img.offsetWidth * (this.zoom - 1)));
      this.y = clamp(d.y - (e.clientY - d.startY) / (img.offsetHeight * (this.zoom - 1)));
      this.transform();
    };
    pane.onpointerup = e => {
      const d = this.drag;
      if (!d || d.id !== e.pointerId) return;
      if (Math.hypot(e.clientX - d.startX, e.clientY - d.startY) < 4) {
        this.x = clamp(d.focusX); this.y = clamp(d.focusY);
        this.setZoom(this.zoom === 1 ? 2 : 1);
      }
      this.drag = null;
    };
    pane.onpointercancel = () => { this.drag = null; };
  }
};
