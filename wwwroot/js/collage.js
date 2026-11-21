/**
 * svacitra - Collage Studio
 * Renders Picture Pile and Grid Mosaics from photos staged in the tray.
 */

window.PicasaCollage = {
  currentType: 'picture_pile',
  collagePath: null,

  init() {
    this.setupTypeSelector();
    this.setupActions();
  },

  setupTypeSelector() {
    document.querySelectorAll('.collage-type-card').forEach(card => {
      card.addEventListener('click', () => {
        document.querySelectorAll('.collage-type-card').forEach(c => c.classList.remove('active'));
        card.classList.add('active');
        this.currentType = card.dataset.type;
        this.renderPreview();
      });
    });
  },

  setupActions() {
    document.getElementById('btn-generate-collage').addEventListener('click', () => {
      this.generateCollage();
    });

    document.getElementById('btn-export-collage').addEventListener('click', async () => {
      if (!this.collagePath) return alert('Create a collage first.');
      const name = this.collagePath.split(/[\\/]/).pop();
      try {
        if (window.__PICASA_NATIVE__) {
          const res = await fetch('/api/collage/export', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name })
          });
          const data = await res.json();
          if (!res.ok) throw new Error(data.error || 'Could not export collage');
          if (data.path) alert('Collage saved to:\n' + data.path);
        } else if (window.showSaveFilePicker) {
          const handle = await window.showSaveFilePicker({ suggestedName: name, types: [
            { description: 'JPEG image', accept: { 'image/jpeg': ['.jpg'] } }
          ] });
          const res = await fetch('/.cache/collages/' + encodeURIComponent(name));
          if (!res.ok) throw new Error('Collage preview is unavailable');
          const stream = await handle.createWritable();
          await stream.write(await res.blob());
          await stream.close();
          alert('Collage saved as ' + handle.name);
        } else {
          const link = document.createElement('a');
          link.href = '/.cache/collages/' + encodeURIComponent(name);
          link.download = name;
          link.click();
        }
      } catch (error) { if (error.name !== 'AbortError') alert(error.message); }
    });
  },

  renderPreview() {
    const container = document.getElementById('collage-preview-container');
    const photos = window.Picasa.state.selectedPhotos.length > 0 
      ? window.Picasa.state.selectedPhotos 
      : window.Picasa.state.photos.slice(0, 8);

    if (!photos.length) {
      container.innerHTML = `
        <div class="empty-state">
          <div class="empty-icon">🖼️</div>
          <h2>Select Photos in Photo Tray</h2>
          <p>Hold photos in your Photo Tray below, then click "Create Collage".</p>
        </div>
      `;
      return;
    }

    this.generateCollage();
  },

  async generateCollage() {
    const container = document.getElementById('collage-preview-container');
    const photos = window.Picasa.state.selectedPhotos.length > 0 
      ? window.Picasa.state.selectedPhotos 
      : window.Picasa.state.photos.slice(0, 8);

    if (!photos.length) {
      alert('Please select or hold photos in the Photo Tray first!');
      return;
    }

    this.collagePath = null;
    container.innerHTML = '<div style="color: #aaa; font-style: italic;">Rendering collage...</div>';

    const bgColor = document.getElementById('collage-bg-color').value || '#18181b';
    const photoIds = photos.map(p => p.id);

    try {
      const res = await fetch('/api/collage/generate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          photo_ids: photoIds,
          type: this.currentType,
          width: 1920,
          height: 1080,
          bg_color: bgColor
        })
      });

      const data = await res.json();
      const esc = window.Picasa.escapeHtml;
      if (data.success) {
        this.collagePath = data.collage_path;
        const fileName = data.collage_path.split(/[\\/]/).pop();
        container.innerHTML = `<img src="/.cache/collages/${encodeURIComponent(fileName)}?t=${Date.now()}" alt="Collage Preview" style="box-shadow: 0 10px 40px rgba(0,0,0,0.8);">`;
      } else {
        container.innerHTML = `<div style="color: #ef4444;">Failed to generate collage: ${esc(data.error)}</div>`;
      }
    } catch (e) {
      const esc = window.Picasa.escapeHtml;
      container.innerHTML = `<div style="color: #ef4444;">Error generating collage: ${esc(e)}</div>`;
    }
  }
};
