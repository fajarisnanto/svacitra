/**
 * svacitra - Photo Tray & Batch Action Bar
 * The iconic Picasa Photo Tray: Hold, Clear, Multi-selection, and Batch Actions.
 */

window.PicasaTray = {
  init() {
    this.setupTrayControls();
    this.setupActionButtons();
  },

  setupTrayControls() {
    const holdBtn = document.getElementById('btn-tray-hold');
    const clearBtn = document.getElementById('btn-tray-clear');

    holdBtn.addEventListener('click', () => {
      window.Picasa.state.isHeld = !window.Picasa.state.isHeld;
      holdBtn.classList.toggle('held', window.Picasa.state.isHeld);
      if (window.Picasa.state.isHeld) {
        window.Picasa.state.heldPhotos = [...window.Picasa.state.selectedPhotos];
      }
    });

    clearBtn.addEventListener('click', () => {
      window.Picasa.state.isHeld = false;
      holdBtn.classList.remove('held');
      window.Picasa.state.selectedPhotos = [];
      window.Picasa.state.heldPhotos = [];
      this.updateTrayUI();
      document.querySelectorAll('.photo-card.selected').forEach(c => c.classList.remove('selected'));
    });
  },

  setupActionButtons() {
    // Star Selected
    document.getElementById('btn-action-star').addEventListener('click', () => this.toggleStarActive());

    // Rotate Left
    document.getElementById('btn-action-rot-left').addEventListener('click', () => this.rotateActive(-90));

    // Rotate Right
    document.getElementById('btn-action-rot-right').addEventListener('click', () => this.rotateActive(90));

    // I'm Feeling Lucky
    document.getElementById('btn-action-lucky').addEventListener('click', () => this.applyFeelingLuckyActive());

    // Luna AI Batch Auto-Tag
    const aiTagBtn = document.getElementById('btn-action-ai-tag');
    if (aiTagBtn) {
      aiTagBtn.addEventListener('click', () => this.runLunaAIBatchTag());
    }

    // Collage
    document.getElementById('btn-action-collage').addEventListener('click', () => {
      if (!window.Picasa.state.selectedPhotos.length) {
        alert('Please select or hold photos in the Photo Tray first!');
        return;
      }
      window.Picasa.switchTab('collage');
      if (window.PicasaCollage) window.PicasaCollage.renderPreview();
    });

    // Slideshow
    document.getElementById('btn-action-slideshow').addEventListener('click', () => {
      const photos = window.Picasa.state.selectedPhotos.length > 0 
        ? window.Picasa.state.selectedPhotos 
        : window.Picasa.state.photos;
      if (photos.length > 0 && window.PicasaSlideshow) {
        window.PicasaSlideshow.start(photos, 0);
      }
    });

    // Export
    document.getElementById('btn-action-export').addEventListener('click', () => this.exportActive());
  },

  selectSingle(photo) {
    if (window.Picasa.state.isHeld) {
      const exists = window.Picasa.state.selectedPhotos.some(p => p.id === photo.id);
      if (exists) {
        window.Picasa.state.selectedPhotos = window.Picasa.state.selectedPhotos.filter(p => p.id !== photo.id);
      } else {
        window.Picasa.state.selectedPhotos.push(photo);
      }
    } else {
      window.Picasa.state.selectedPhotos = [photo];
    }
    this.updateTrayUI();
    this.syncCardSelection();
  },

  toggleSelect(photo) {
    const exists = window.Picasa.state.selectedPhotos.some(p => p.id === photo.id);
    if (exists) {
      window.Picasa.state.selectedPhotos = window.Picasa.state.selectedPhotos.filter(p => p.id !== photo.id);
    } else {
      window.Picasa.state.selectedPhotos.push(photo);
    }
    this.updateTrayUI();
    this.syncCardSelection();
  },

  syncCardSelection() {
    const selectedIds = new Set(window.Picasa.state.selectedPhotos.map(p => p.id));
    document.querySelectorAll('.photo-card').forEach(card => {
      const id = parseInt(card.dataset.id, 10);
      card.classList.toggle('selected', selectedIds.has(id));
    });
  },

  updateTrayUI() {
    const carousel = document.getElementById('tray-carousel');
    const photos = window.Picasa.state.selectedPhotos;

    if (!photos.length) {
      carousel.innerHTML = '<div class="tray-empty-hint">No photos selected. Click any photo to select.</div>';
      return;
    }

    const esc = window.Picasa.escapeHtml;
    carousel.innerHTML = photos.map(p => `
      <div class="tray-thumb-card" data-id="${p.id}" title="${esc(p.filename)}">
        <img src="/api/thumb/${p.id}" alt="${esc(p.filename)}">
        <span class="tray-thumb-remove" data-id="${p.id}">✕</span>
      </div>
    `).join('');

    carousel.querySelectorAll('.tray-thumb-remove').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const id = parseInt(btn.dataset.id, 10);
        window.Picasa.state.selectedPhotos = window.Picasa.state.selectedPhotos.filter(p => p.id !== id);
        this.updateTrayUI();
        this.syncCardSelection();
      });
    });

    carousel.querySelectorAll('.tray-thumb-card').forEach(card => {
      card.addEventListener('dblclick', () => {
        const id = parseInt(card.dataset.id, 10);
        const idx = window.Picasa.state.photos.findIndex(p => p.id === id);
        if (idx !== -1) window.PicasaViewer.open(idx);
      });
    });
  },

  async runLunaAIBatchTag() {
    const photos = window.Picasa.state.selectedPhotos;
    if (!photos.length) {
      alert('Please select or hold photos in the Photo Tray to analyze with GPT-6 Luna!');
      return;
    }

    const tagBtn = document.getElementById('btn-action-ai-tag');
    tagBtn.innerText = '🤖 Tagging...';

    let successCount = 0;
    let fallbackCount = 0;
    let lastError = null;

    for (const p of photos) {
      try {
        const res = await fetch('/api/ai/analyze', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ photo_id: p.id })
        });
        let data;
        try {
          data = await res.json();
        } catch (jsonErr) {
          const raw = await res.text().catch(() => '');
          data = { success: false, error: raw };
        }

        if (!data.success) {
          lastError = data.error;
          // Auto fallback to heuristic for batch so user still gets tagged photos!
          const fRes = await fetch('/api/ai/analyze', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ photo_id: p.id, fallback_heuristic: true })
          });
          data = await fRes.json();
          if (data.success) fallbackCount++;
        } else {
          successCount++;
        }

        if (data.success && data.photo) {
          p.ai_processed = true;
          p.ai_tags = data.photo.ai_tags;
          p.ai_caption = data.photo.ai_caption;
          p.ai_fixes = data.photo.ai_fixes;
          window.Picasa.emit('photoUpdated', p);
        }
      } catch (e) {
        console.error('Error analyzing photo:', e);
      }
    }

    tagBtn.innerHTML = '<span class="action-icon">🤖</span> Luna Tag';
    if (fallbackCount > 0 && lastError) {
      alert(`Luna Tagged ${photos.length} photo(s).\n\nNotice: Cloud API returned:\n"${lastError}"\n\nLuna automatically applied offline smart heuristic analysis.`);
    } else {
      alert(`Luna AI finished analyzing and tagging ${photos.length} photo(s)!`);
    }
    if (window.PicasaLibrary) window.PicasaLibrary.refresh();
  },

  async toggleStarActive() {
    const photos = window.Picasa.state.selectedPhotos;
    if (!photos.length) return;

    for (const p of photos) {
      try {
        const res = await fetch(`/api/photo/${p.id}/star`, { method: 'POST' });
        const data = await res.json();
        p.is_starred = data.is_starred;
        window.Picasa.emit('photoUpdated', p);
      } catch (e) {}
    }
  },

  async rotateActive(delta) {
    const photos = window.Picasa.state.selectedPhotos;
    if (!photos.length) return;

    for (const p of photos) {
      try {
        const res = await fetch(`/api/photo/${p.id}/rotate`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ delta })
        });
        const data = await res.json();
        p.rotation = data.rotation;
        window.Picasa.emit('photoUpdated', p);
      } catch (e) {}
    }
  },

  async applyFeelingLuckyActive() {
    const photos = window.Picasa.state.selectedPhotos;
    if (!photos.length) return;

    for (const p of photos) {
      const edits = { ...(p.edits || {}), feeling_lucky: true };
      try {
        const res = await fetch(`/api/photo/${p.id}/save-edit`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ edits })
        });
        if (res.ok) {
          p.edits = edits;
          window.Picasa.emit('photoUpdated', p);
        }
      } catch (e) {}
    }
    alert(`Applied "I'm Feeling Lucky" auto-enhancement to ${photos.length} photo(s)!`);
  },

  async exportActive() {
    const photos = window.Picasa.state.selectedPhotos;
    if (!photos.length) {
      alert('Please select photo(s) to export.');
      return;
    }

    const first = photos[0];
    try {
      const res = await fetch('/api/export', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ photo_id: first.id })
      });
      const data = await res.json();
      if (data.exported) {
        alert(`Exported photo copy to:\n${data.path}`);
      }
    } catch (e) {
      alert('Export failed: ' + e);
    }
  }
};
