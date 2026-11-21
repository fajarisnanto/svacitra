/**
 * svacitra - Photo Viewer
 * Dark semi-transparent lightbox, cursor-anchored zoom, pan, and bottom filmstrip.
 * Displays GPT-6 Luna AI analysis overlay.
 */

window.PicasaViewer = {
  currentIndex: -1,
  scale: 1,
  translateX: 0,
  translateY: 0,
  isDragging: false,
  dragStartX: 0,
  dragStartY: 0,

  init() {
    this.setupControls();
    this.setupZoomPan();
  },

  setupControls() {
    const viewer = document.getElementById('picasa-viewer');
    const closeBtn = document.getElementById('viewer-btn-close');
    const prevBtn = document.getElementById('viewer-nav-prev');
    const nextBtn = document.getElementById('viewer-nav-next');
    const starBtn = document.getElementById('viewer-btn-star');
    const editBtn = document.getElementById('viewer-btn-edit');
    const rotL = document.getElementById('viewer-btn-rotate-left');
    const rotR = document.getElementById('viewer-btn-rotate-right');
    const revealBtn = document.getElementById('viewer-btn-reveal');
    const aiBtn = document.getElementById('viewer-btn-ai-analyze');

    closeBtn.addEventListener('click', () => this.close());
    prevBtn.addEventListener('click', () => this.prev());
    nextBtn.addEventListener('click', () => this.next());

    starBtn.addEventListener('click', async () => {
      const photo = this.getCurrentPhoto();
      if (!photo) return;
      try {
        const res = await fetch(`/api/photo/${photo.id}/star`, { method: 'POST' });
        const data = await res.json();
        photo.is_starred = data.is_starred;
        starBtn.innerText = photo.is_starred ? '⭐ Starred' : '⭐';
        window.Picasa.emit('photoUpdated', photo);
      } catch (e) {}
    });

    editBtn.addEventListener('click', () => {
      const photo = this.getCurrentPhoto();
      if (photo) {
        this.close();
        window.PicasaEditor.openPhoto(photo);
      }
    });

    rotL.addEventListener('click', () => this.rotateCurrent(-90));
    rotR.addEventListener('click', () => this.rotateCurrent(90));

    revealBtn.addEventListener('click', async () => {
      const photo = this.getCurrentPhoto();
      if (!photo) return;
      try {
        await fetch('/api/reveal', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: photo.file_path })
        });
      } catch (e) {}
    });

    if (aiBtn) {
      aiBtn.addEventListener('click', async () => {
        const photo = this.getCurrentPhoto();
        if (!photo) return;
        aiBtn.innerText = '🤖 Analyzing...';
        try {
          const res = await fetch('/api/ai/analyze', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ photo_id: photo.id })
          });
          
          let data;
          try {
            data = await res.json();
          } catch (jsonErr) {
            const raw = await res.text().catch(() => '');
            throw new Error(raw || 'Failed to parse server response');
          }

          if (data.success && data.photo) {
            photo.ai_processed = true;
            photo.ai_caption = data.photo.ai_caption;
            photo.ai_tags = data.photo.ai_tags;
            this.updateAIOverlay(photo);
            window.Picasa.emit('photoUpdated', photo);
          } else if (!data.success) {
            const errorMsg = data.error || 'AI analysis failed.';
            const useFallback = confirm(`${errorMsg}\n\nWould you like to analyze this photo using Luna's built-in offline Smart Heuristic engine instead?`);
            if (useFallback) {
              aiBtn.innerText = '🤖 Using Heuristic...';
              const fRes = await fetch('/api/ai/analyze', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ photo_id: photo.id, fallback_heuristic: true })
              });
              const fData = await fRes.json();
              if (fData.success && fData.photo) {
                photo.ai_processed = true;
                photo.ai_caption = fData.photo.ai_caption;
                photo.ai_tags = fData.photo.ai_tags;
                this.updateAIOverlay(photo);
                window.Picasa.emit('photoUpdated', photo);
              }
            }
          }
        } catch (e) {
          alert('Luna AI analysis: ' + (e.message || e));
        } finally {
          aiBtn.innerText = '🤖 Luna Analyze';
        }
      });
    }
  },

  setupZoomPan() {
    const viewport = document.getElementById('viewer-viewport');
    const img = document.getElementById('viewer-main-img');

    viewport.addEventListener('wheel', (e) => {
      e.preventDefault();
      const zoomFactor = e.deltaY < 0 ? 1.2 : 0.83;
      const newScale = Math.min(10, Math.max(0.5, this.scale * zoomFactor));

      if (newScale !== this.scale) {
        const rect = img.getBoundingClientRect();
        const cursorX = e.clientX - rect.left - (rect.width / 2);
        const cursorY = e.clientY - rect.top - (rect.height / 2);

        this.translateX -= (cursorX / this.scale) * (newScale - this.scale);
        this.translateY -= (cursorY / this.scale) * (newScale - this.scale);
        this.scale = newScale;

        if (this.scale <= 1) {
          this.translateX = 0;
          this.translateY = 0;
        }

        this.updateTransform();
      }
    }, { passive: false });

    viewport.addEventListener('mousedown', (e) => {
      if (this.scale > 1) {
        this.isDragging = true;
        this.dragStartX = e.clientX - this.translateX;
        this.dragStartY = e.clientY - this.translateY;
        viewport.classList.add('dragging');
      }
    });

    window.addEventListener('mousemove', (e) => {
      if (this.isDragging) {
        this.translateX = e.clientX - this.dragStartX;
        this.translateY = e.clientY - this.dragStartY;
        this.updateTransform();
      }
    });

    window.addEventListener('mouseup', () => {
      this.isDragging = false;
      viewport.classList.remove('dragging');
    });

    viewport.addEventListener('dblclick', (e) => {
      if (e.target.tagName === 'BUTTON') return;
      if (this.scale > 1) {
        this.resetZoom();
      } else {
        this.scale = 2.5;
        this.updateTransform();
      }
    });
  },

  resetZoom() {
    this.scale = 1;
    this.translateX = 0;
    this.translateY = 0;
    this.updateTransform();
  },

  updateTransform() {
    const img = document.getElementById('viewer-main-img');
    img.style.transform = `translate(${this.translateX}px, ${this.translateY}px) scale(${this.scale})`;
  },

  open(index) {
    const photos = window.Picasa.state.photos;
    if (!photos.length) return;
    if (index < 0) index = 0;
    if (index >= photos.length) index = photos.length - 1;

    this.currentIndex = index;
    const photo = photos[index];

    const viewer = document.getElementById('picasa-viewer');
    viewer.style.display = 'flex';

    this.resetZoom();
    this.renderCurrentPhoto();
    this.renderFilmstrip();
  },

  openPhoto(photo) {
    const photos = window.Picasa.state.photos;
    let idx = photos.findIndex(p => p.id === photo.id);
    if (idx === -1) {
      photos.push(photo);
      idx = photos.length - 1;
    }
    this.open(idx);
  },

  close() {
    const viewer = document.getElementById('picasa-viewer');
    viewer.style.display = 'none';
    this.resetZoom();
  },

  getCurrentPhoto() {
    const photos = window.Picasa.state.photos;
    if (this.currentIndex >= 0 && this.currentIndex < photos.length) {
      return photos[this.currentIndex];
    }
    return null;
  },

  next() {
    const photos = window.Picasa.state.photos;
    if (this.currentIndex < photos.length - 1) {
      this.currentIndex++;
      this.resetZoom();
      this.renderCurrentPhoto();
      this.updateFilmstripActive();
    }
  },

  prev() {
    if (this.currentIndex > 0) {
      this.currentIndex--;
      this.resetZoom();
      this.renderCurrentPhoto();
      this.updateFilmstripActive();
    }
  },

  renderCurrentPhoto() {
    const photo = this.getCurrentPhoto();
    if (!photo) return;

    const img = document.getElementById('viewer-main-img');
    const filename = document.getElementById('viewer-filename');
    const meta = document.getElementById('viewer-meta');
    const starBtn = document.getElementById('viewer-btn-star');

    img.src = `/api/image/${photo.id}?max_dim=1920&t=${Date.now()}`;
    filename.innerText = photo.filename;

    const sizeMB = photo.file_size ? (photo.file_size / (1024 * 1024)).toFixed(1) + ' MB' : '';
    const dims = photo.width && photo.height ? `${photo.width} × ${photo.height}` : '';
    meta.innerText = [dims, sizeMB].filter(Boolean).join(' • ');

    starBtn.innerText = photo.is_starred ? '⭐ Starred' : '⭐';

    this.updateAIOverlay(photo);
    window.PicasaTray.selectSingle(photo);
  },

  updateAIOverlay(photo) {
    const bar = document.getElementById('viewer-ai-bar');
    const cap = document.getElementById('viewer-ai-caption');
    const tagsContainer = document.getElementById('viewer-ai-tags');

    if (photo.ai_caption || (photo.ai_tags && photo.ai_tags.length)) {
      const esc = window.Picasa.escapeHtml;
      bar.style.display = 'flex';
      cap.innerText = photo.ai_caption ? `🤖 Luna: "${photo.ai_caption}"` : '';
      tagsContainer.innerHTML = (photo.ai_tags || []).map(t => `<span class="tag-pill">#${esc(t)}</span>`).join('');
    } else {
      bar.style.display = 'none';
    }
  },

  renderFilmstrip() {
    const track = document.getElementById('filmstrip-track');
    const allPhotos = window.Picasa.state.photos;
    if (!allPhotos.length) {
      track.innerHTML = '';
      return;
    }

    const currentPhoto = this.getCurrentPhoto();
    let photos = allPhotos;
    if (currentPhoto && currentPhoto.folder_path) {
      const folderPhotos = allPhotos.filter(p => p.folder_path === currentPhoto.folder_path);
      if (folderPhotos.length > 0) photos = folderPhotos;
    }

    const activeIdx = currentPhoto ? photos.findIndex(p => p.id === currentPhoto.id) : 0;
    let start = 0;
    let end = photos.length;
    if (photos.length > 60) {
      start = Math.max(0, activeIdx - 30);
      end = Math.min(photos.length, start + 60);
    }
    const visiblePhotos = photos.slice(start, end);

    const esc = window.Picasa.escapeHtml;
    track.innerHTML = visiblePhotos.map(p => `
      <div class="filmstrip-thumb ${currentPhoto && p.id === currentPhoto.id ? 'active' : ''}" data-id="${p.id}" title="${esc(p.filename)}">
        <img src="/api/thumb/${p.id}" alt="${esc(p.filename)}" loading="lazy">
      </div>
    `).join('');

    track.querySelectorAll('.filmstrip-thumb').forEach(thumb => {
      thumb.addEventListener('click', () => {
        const id = parseInt(thumb.dataset.id, 10);
        const targetPhoto = allPhotos.find(p => p.id === id);
        if (targetPhoto) {
          const globalIdx = allPhotos.findIndex(p => p.id === id);
          this.currentIndex = globalIdx !== -1 ? globalIdx : this.currentIndex;
          this.resetZoom();
          this.renderCurrentPhoto();
          this.renderFilmstrip();
        }
      });
    });

    const active = track.querySelector('.filmstrip-thumb.active');
    if (active) {
      active.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
    }
  },

  updateFilmstripActive() {
    document.querySelectorAll('.filmstrip-thumb').forEach((thumb, idx) => {
      thumb.classList.toggle('active', idx === this.currentIndex);
    });
    this.scrollFilmstripToActive();
  },

  scrollFilmstripToActive() {
    const active = document.querySelector('.filmstrip-thumb.active');
    if (active) {
      active.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
    }
  },

  async rotateCurrent(delta) {
    const photo = this.getCurrentPhoto();
    if (!photo) return;
    try {
      const res = await fetch(`/api/photo/${photo.id}/rotate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ delta })
      });
      const data = await res.json();
      photo.rotation = data.rotation;
      this.renderCurrentPhoto();
      window.Picasa.emit('photoUpdated', photo);
    } catch (e) {}
  }
};
