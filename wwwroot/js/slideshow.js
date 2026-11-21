/**
 * svacitra - Fullscreen Slideshow Player
 * Cinematic presentation with auto-advance, fade transition, and keyboard navigation.
 */

window.PicasaSlideshow = {
  photos: [],
  currentIndex: 0,
  isPlaying: true,
  timer: null,
  intervalMs: 3500,

  init() {
    const prevBtn = document.getElementById('ss-btn-prev');
    const nextBtn = document.getElementById('ss-btn-next');
    const playBtn = document.getElementById('ss-btn-play');
    const closeBtn = document.getElementById('ss-btn-close');

    prevBtn.addEventListener('click', () => this.prev());
    nextBtn.addEventListener('click', () => this.next());
    playBtn.addEventListener('click', () => this.togglePlay());
    document.getElementById('ss-btn-export').addEventListener('click', () => this.exportVideo());
    closeBtn.addEventListener('click', () => this.stop());
  },

  start(photos, startIndex = 0) {
    if (!photos || !photos.length) return;
    this.photos = photos.slice();
    this.startIndex = startIndex;
    document.getElementById('ss-export-status').textContent = '';
    document.getElementById('ss-btn-play').innerText = '❚❚';
    this.currentIndex = startIndex;
    this.isPlaying = true;

    const overlay = document.getElementById('picasa-slideshow');
    overlay.style.display = 'flex';

    this.showCurrentSlide();
    this.startTimer();
  },

  async exportVideo() {
    if (this.exportController) {
      this.exportController.abort();
      return;
    }
    if (!this.photos.length) return;
    if (this.isPlaying) this.togglePlay();
    const ordered = this.photos.slice(this.startIndex).concat(this.photos.slice(0, this.startIndex));
    const controller = this.exportController = new AbortController();
    const button = document.getElementById('ss-btn-export');
    const status = document.getElementById('ss-export-status');
    button.textContent = 'Cancel export';
    status.textContent = 'Choose where to save…';
    const poll = setInterval(async () => {
      try {
        const res = await fetch('/api/slideshow/export-status', { signal: controller.signal });
        const progress = await res.json();
        if (this.exportController === controller)
          status.textContent = 'Exporting ' + progress.completed + ' / ' + ordered.length;
      } catch { /* The export request reports any final error. */ }
    }, 1000);
    try {
      const res = await fetch('/api/slideshow/export', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        signal: controller.signal, body: JSON.stringify({ photo_ids: ordered.map(p => p.id) })
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || 'Could not export slideshow.');
      status.textContent = data.path ? 'MP4 saved' : 'Export cancelled';
      status.title = data.path || '';
    } catch (error) {
      status.textContent = controller.signal.aborted ? 'Export cancelled' : error.message;
    } finally {
      clearInterval(poll);
      this.exportController = null;
      button.textContent = 'Export MP4…';
    }
  },

  stop() {
    this.exportController?.abort();
    clearInterval(this.timer);
    const overlay = document.getElementById('picasa-slideshow');
    overlay.style.display = 'none';
  },

  togglePlay() {
    this.isPlaying = !this.isPlaying;
    const playBtn = document.getElementById('ss-btn-play');
    playBtn.innerText = this.isPlaying ? '❚❚' : '▶';

    if (this.isPlaying) {
      this.startTimer();
    } else {
      clearInterval(this.timer);
    }
  },

  startTimer() {
    clearInterval(this.timer);
    this.timer = setInterval(() => {
      this.next();
    }, this.intervalMs);
  },

  next() {
    if (!this.photos.length) return;
    this.currentIndex = (this.currentIndex + 1) % this.photos.length;
    this.showCurrentSlide();
  },

  prev() {
    if (!this.photos.length) return;
    this.currentIndex = (this.currentIndex - 1 + this.photos.length) % this.photos.length;
    this.showCurrentSlide();
  },

  showCurrentSlide() {
    const photo = this.photos[this.currentIndex];
    if (!photo) return;

    const img = document.getElementById('slideshow-img');
    img.style.opacity = '0';

    setTimeout(() => {
      img.src = `/api/image/${photo.id}?max_dim=1920&t=${Date.now()}`;
      img.onload = () => {
        img.style.opacity = '1';
      };
    }, 150);
  }
};
