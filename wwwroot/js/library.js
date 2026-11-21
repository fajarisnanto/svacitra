/**
 * svacitra - Library & Timeline Engine
 * Renders folders, virtualized photo grids, timeline scrubber, and folder trees.
 */

window.PicasaLibrary = {
  activeFilter: 'all', // 'all', 'starred', 'folder', 'ai-tagged'
  activeFolderPath: null,

  init() {
    this.chunks = new Map();
    const gallery = document.getElementById('gallery-content');
    this.viewport = new IntersectionObserver(entries => {
      for (const entry of entries) {
        const chunk = this.chunks.get(entry.target);
        if (!chunk) continue;
        if (entry.isIntersecting && !entry.target.childElementCount) {
          entry.target.replaceChildren(...chunk.map(photo => this.createPhotoCard(photo)));
        } else if (!entry.isIntersecting) {
          entry.target.replaceChildren();
        }
      }
    }, { root: document.getElementById('gallery-scroll-area'), rootMargin: '360px 0px' });
    this.gallerySize = new ResizeObserver(() => this.sizeChunks());
    this.gallerySize.observe(gallery);
    this.setupSidebarFilters();
    this.setupZoomSlider();
    this.setupScopeChips();
    this.setupSlideshowAll();
    this.loadLibrary();

    // Listen for events
    window.Picasa.on('searchChanged', () => this.loadPhotos());
    window.Picasa.on('photoUpdated', (photo) => this.updatePhotoInGrid(photo));
  },

  setupSidebarFilters() {
    document.querySelectorAll('.sidebar-item[data-filter]').forEach(item => {
      item.addEventListener('click', () => {
        document.querySelectorAll('.sidebar-item').forEach(i => i.classList.remove('active'));
        item.classList.add('active');

        const filter = item.dataset.filter;
        this.activeFilter = filter;
        this.activeFolderPath = null;
        window.Picasa.state.starredOnly = (filter === 'starred');
        window.Picasa.state.aiTaggedOnly = (filter === 'ai-tagged');
        window.Picasa.state.activeFolder = null;

        this.updateScopeHeader(item.querySelector('.item-title').innerText);
        this.loadPhotos();
      });
    });
  },

  setupScopeChips() {
    document.querySelectorAll('.chip-filter').forEach(chip => {
      chip.addEventListener('click', () => {
        document.querySelectorAll('.chip-filter').forEach(c => c.classList.remove('active'));
        chip.classList.add('active');
        const mode = chip.dataset.chip;
        window.Picasa.state.starredOnly = (mode === 'starred');
        window.Picasa.state.aiTaggedOnly = (mode === 'ai-tagged');
        this.loadPhotos();
      });
    });
  },

  sizeChunks() {
    const first = this.chunks.keys().next().value;
    if (!first || !first.clientWidth) return;
    const style = getComputedStyle(first);
    const columns = style.gridTemplateColumns.split(' ').length;
    const gap = parseFloat(style.rowGap);
    const size = (first.clientWidth - (columns - 1) * gap) / columns;
    for (const [grid, photos] of this.chunks) {
      const rows = Math.ceil(photos.length / columns);
      grid.style.height = (rows * size + (rows - 1) * gap) + 'px';
    }
  },

  setupZoomSlider() {
    const slider = document.getElementById('grid-zoom-slider');
    slider.addEventListener('input', (e) => {
      const size = e.target.value;
      document.documentElement.style.setProperty('--thumb-size', `${size}px`);
      this.sizeChunks();
    });
  },

  setupSlideshowAll() {
    document.getElementById('btn-slideshow-all').addEventListener('click', () => {
      const photos = window.Picasa.state.photos;
      if (photos.length > 0 && window.PicasaSlideshow) {
        window.PicasaSlideshow.start(photos, 0);
      }
    });
  },

  updateScopeHeader(title, count = null) {
    const scopeLabel = document.getElementById('current-scope-label');
    const textEl = scopeLabel.querySelector('.scope-text');
    const badgeEl = document.getElementById('scope-count');
    textEl.innerText = title;
    if (count !== null) {
      badgeEl.innerText = `${count} photos`;
    }
  },

  // Refreshes sidebar counts, folder list and timeline without re-rendering the gallery.
  async loadSummary() {
    const res = await fetch('/api/summary');
    const data = await res.json();

    // Counts
    document.getElementById('count-all').innerText = data.total_photos || 0;
    document.getElementById('count-starred').innerText = data.total_starred || 0;
    const aiCountEl = document.getElementById('count-ai-tagged');
    if (aiCountEl) aiCountEl.innerText = data.ai_tagged_count || 0;

    // Folders list in sidebar
    this.renderFoldersList(data.folders || []);

    // Timeline track
    this.renderTimelineTrack(data.timeline || []);
    if (data.ai_settings) window.Picasa.state.aiSettings = data.ai_settings;
    return data;
  },

  async loadLibrary() {
    try {
      await this.loadSummary();

      // Initial photos load
      await this.loadPhotos();
    } catch (err) {
      console.error('Error loading library summary:', err);
    }
  },

  renderFoldersList(folders) {
    const list = document.getElementById('folders-tree-list');
    if (!folders.length) {
      list.innerHTML = '<li class="sidebar-loading">No folders indexed yet.</li>';
      return;
    }

    const esc = window.Picasa.escapeHtml;
    list.innerHTML = folders.map(f => {
      const name = f.folder_path.split(/[\\/]/).pop() || f.folder_path;
      const isActive = this.activeFolderPath && this.activeFolderPath.toLowerCase() === f.folder_path.toLowerCase();
      return `
        <li class="sidebar-item folder-tree-item ${isActive ? 'active' : ''}" data-path="${encodeURIComponent(f.folder_path)}" title="${esc(f.folder_path)}&#10;(right-click to remove from library)">
          <span class="item-icon">📁</span>
          <span class="item-title">${esc(name)}${f.offline ? ' · Offline' : ''}</span>
          <span class="item-count">${f.count}</span>
        </li>
      `;
    }).join('');

    list.querySelectorAll('.folder-tree-item').forEach(item => {
      item.addEventListener('click', () => {
        document.querySelectorAll('.sidebar-item').forEach(i => i.classList.remove('active'));
        item.classList.add('active');

        // Reset starred / AI-tagged subfilters so folder photos always show
        window.Picasa.state.starredOnly = false;
        window.Picasa.state.aiTaggedOnly = false;
        document.querySelectorAll('.chip-filter').forEach(c => {
          c.classList.toggle('active', c.dataset.chip === 'all');
        });

        const path = decodeURIComponent(item.dataset.path);
        this.activeFilter = 'folder';
        this.activeFolderPath = path;
        window.Picasa.state.activeFolder = path;

        this.updateScopeHeader(item.querySelector('.item-title').innerText);
        this.loadPhotos();
      });
    });
  },

  renderTimelineTrack(timeline) {
    const track = document.getElementById('timeline-track');
    const years = [...new Set(timeline.map(t => t.year))].filter(Boolean).slice(0, 10);
    if (!years.length) {
      track.innerHTML = '';
      return;
    }

    track.innerHTML = years.map(y => `<div class="timeline-tick" data-year="${y}">${y}</div>`).join('');
    track.querySelectorAll('.timeline-tick').forEach(tick => {
      tick.addEventListener('click', () => {
        const year = tick.dataset.year;
        const target = document.querySelector(`.folder-group[data-year="${year}"]`);
        if (target) {
          target.scrollIntoView({ behavior: 'smooth' });
        }
      });
    });
  },

  async loadPhotos() {
    const container = document.getElementById('gallery-content');
    const request = this.photoRequest = (this.photoRequest || 0) + 1;
    const params = new URLSearchParams();

    if (this.activeFolderPath) params.append('folder', this.activeFolderPath);
    if (window.Picasa.state.starredOnly) params.append('starred', '1');
    if (window.Picasa.state.aiTaggedOnly) params.append('ai_tagged', '1');
    if (window.Picasa.state.searchQuery) params.append('search', window.Picasa.state.searchQuery);
    const recentOnly = this.activeFilter === 'recent' && !this.activeFolderPath;
    if (recentOnly) {
      params.append('recent', '1');
      params.append('limit', '200');
    }

    try {
      const res = await fetch(`/api/photos?${params.toString()}`);
      const data = await res.json();
      if (request !== this.photoRequest) return;
      this.viewport.disconnect();
      this.chunks.clear();
      const photos = data.photos || [];
      const scope = params.toString();
      if (this.galleryScope !== scope) {
        this.rangeAnchor = null;
        if (this.galleryScope !== undefined && !window.Picasa.state.isHeld) {
          window.Picasa.state.selectedPhotos = [];
          window.PicasaTray.updateTrayUI();
        }
        this.galleryScope = scope;
      }
      this.galleryOrder = [];

      window.Picasa.state.photos = photos;
      let headerTitle = 'All Photos';
      if (this.activeFolderPath) headerTitle = this.activeFolderPath.split(/[\\/]/).pop();
      else if (window.Picasa.state.starredOnly) headerTitle = 'Starred Photos';
      else if (window.Picasa.state.aiTaggedOnly) headerTitle = 'Luna AI Tagged Photos';
      else if (recentOnly) headerTitle = 'Recently Added';

      this.updateScopeHeader(headerTitle, photos.length);

      if (!photos.length) {
        container.innerHTML = `
          <div class="gallery-empty-state">
            <div class="empty-icon">🖼️</div>
            <h2>No Photos Found</h2>
            <p>Try clearing filters or search query, or add a new folder to scan.</p>
            <button class="picasa-btn primary-btn" id="btn-empty-reset-filters" style="margin-top: 14px; padding: 8px 18px;">Show All Photos</button>
          </div>
        `;
        document.getElementById('btn-empty-reset-filters')?.addEventListener('click', () => {
          this.activeFilter = 'all';
          this.activeFolderPath = null;
          window.Picasa.state.starredOnly = false;
          window.Picasa.state.aiTaggedOnly = false;
          window.Picasa.state.searchQuery = '';
          const s = document.getElementById('global-search');
          if (s) s.value = '';
          document.querySelectorAll('.sidebar-item').forEach(i => i.classList.toggle('active', i.dataset.filter === 'all'));
          document.querySelectorAll('.chip-filter').forEach(c => c.classList.toggle('active', c.dataset.chip === 'all'));
          this.loadPhotos();
        });
        return;
      }

      this.renderGroupedGallery(photos);
    } catch (err) {
      console.error('Error loading photos:', err);
    }
  },

  renderGroupedGallery(photos) {
    const container = document.getElementById('gallery-content');
    container.innerHTML = '';

    const groups = Object.create(null);
    photos.forEach(photo => {
      const folder = photo.folder_path;
      if (!groups[folder]) groups[folder] = [];
      groups[folder].push(photo);
    });

    this.galleryOrder = Object.values(groups).flat();
    for (const [folderPath, folderPhotos] of Object.entries(groups)) {
      const folderName = folderPath.split(/[\\/]/).pop() || folderPath;
      const firstPhoto = folderPhotos[0];
      const year = firstPhoto.year || '';
      const dateStr = firstPhoto.date_taken ? new Date(firstPhoto.date_taken).toLocaleDateString(undefined, { year: 'numeric', month: 'short' }) : '';

      const groupEl = document.createElement('div');
      groupEl.className = 'folder-group';
      groupEl.dataset.year = year;

      const esc = window.Picasa.escapeHtml;
      groupEl.innerHTML = `
        <div class="folder-header-banner">
          <div class="folder-header-left">
            <span class="folder-name">${esc(folderName)}</span>
            <span class="folder-meta">• ${esc(dateStr)} • ${folderPhotos.length} photos</span>
          </div>
          <div class="folder-actions">
            <button class="folder-play-btn play-group-ss">${window.PicasaIcons.html("play")}Slideshow</button>
          </div>
        </div>
        <div class="photos-grid"></div>
      `;

      groupEl.querySelector('.play-group-ss').addEventListener('click', (e) => {
        e.stopPropagation();
        if (window.PicasaSlideshow) window.PicasaSlideshow.start(folderPhotos, 0);
      });

      const grid = groupEl.querySelector('.photos-grid');
      for (let i = 0; i < folderPhotos.length; i += 60) {
        const chunk = i === 0 ? grid : document.createElement('div');
        if (i) {
          chunk.className = 'photos-grid';
          chunk.style.marginTop = '10px';
          groupEl.appendChild(chunk);
        }
        this.chunks.set(chunk, folderPhotos.slice(i, i + 60));
        this.viewport.observe(chunk);
      }

      container.appendChild(groupEl);
    }
    this.sizeChunks();
  },

  createPhotoCard(photo) {
    const card = document.createElement('div');
    card.className = `photo-card ${photo.is_starred ? 'is-starred' : ''}`;
    card.dataset.id = photo.id;

    const isSelected = window.PicasaTray.selectedIds.has(photo.id);
    if (isSelected) card.classList.add('selected');

    const esc = window.Picasa.escapeHtml;
    const aiBadge = photo.ai_processed ? `<div class="card-ai-badge" title="Analyzed with GPT-6 Luna">${window.PicasaIcons.html('sparkles')}Luna</div>` : '';
    const hoverTitle = photo.ai_caption ? `🤖 ${photo.ai_caption}` : photo.filename;
    const safeFilename = esc(photo.filename);
    const safeHover = esc(hoverTitle);

    card.innerHTML = `
      <img src="${this.thumbUrl(photo.id)}" loading="lazy" alt="${safeFilename}">
      ${aiBadge}
      ${photo.offline ? '<span class="offline-badge">Offline</span>' : ''}
      <div class="card-star-badge" title="Toggle Star">${window.PicasaIcons.html('star')}</div>
      <div class="card-title-hover" title="${safeHover}">${safeHover}</div>
    `;

    // Star click
    const starBtn = card.querySelector('.card-star-badge');
    starBtn.addEventListener('click', async (e) => {
      e.stopPropagation();
      try {
        const res = await fetch(`/api/photo/${photo.id}/star`, { method: 'POST' });
        const data = await res.json();
        photo.is_starred = data.is_starred;
        card.classList.toggle('is-starred', data.is_starred === 1);
        window.Picasa.emit('photoStarToggled', photo);
      } catch (err) {}
    });

    // Single click: select/add to photo tray
    card.addEventListener('click', (e) => {
      const tray = window.PicasaTray;
      const additive = e.ctrlKey || e.metaKey;
      if (e.shiftKey) {
        const start = this.galleryOrder.findIndex(p => p.id === this.rangeAnchor);
        const end = this.galleryOrder.findIndex(p => p.id === photo.id);
        const anchored = start >= 0 && tray.selectedIds.has(this.rangeAnchor);
        const range = anchored ? this.galleryOrder.slice(Math.min(start, end), Math.max(start, end) + 1) : [photo];
        if (!anchored) this.rangeAnchor = photo.id;
        tray.selectRange(range, additive || window.Picasa.state.isHeld);
      } else {
        this.rangeAnchor = photo.id;
        if (additive) tray.toggleSelect(photo);
        else tray.selectSingle(photo);
      }
    });

    // Double click: in classic Google Picasa, double clicking a photo enters the Single Photo Studio (Editor)
    card.addEventListener('dblclick', (e) => {
      e.stopPropagation();
      if (!e.shiftKey && !e.ctrlKey && !e.metaKey) window.PicasaEditor.openPhoto(photo);
    });

    return card;
  },

  thumbVersions: new Map(),

  thumbUrl(id) {
    const version = this.thumbVersions.get(id);
    return '/api/thumb/' + id + (version ? '?t=' + version : '');
  },

  updatePhotoInGrid(photo) {
    this.thumbVersions.set(photo.id, Date.now());
    const stored = window.Picasa.state.photos.find(p => p.id === photo.id);
    if (stored) Object.assign(stored, photo);
    const card = document.querySelector(`.photo-card[data-id="${photo.id}"]`);
    if (card) {
      card.classList.toggle('is-starred', photo.is_starred === 1);
      const img = card.querySelector('img');
      img.src = this.thumbUrl(photo.id);
      if (photo.ai_processed && !card.querySelector('.card-ai-badge')) {
        const badge = document.createElement('div');
        badge.className = 'card-ai-badge';
        badge.title = 'Analyzed with GPT-6 Luna';
        badge.innerHTML = window.PicasaIcons.html('sparkles') + 'Luna';
        card.appendChild(badge);
      }
    }
  },

  refresh() {
    this.loadLibrary();
  }
};
