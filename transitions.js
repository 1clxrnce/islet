(function () {
  var root = document.documentElement;
  var overlay = document.querySelector('.page-wipe');
  var nav = document.querySelector('.menu');
  var links = nav ? Array.prototype.slice.call(nav.querySelectorAll('a[href]')) : [];
  var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
  var motionToggle = document.querySelector('.motion-toggle');
  var navigating = false;
  var aimRay = function () {};
  var resetGaze = function () {};

  function motionAllowed() {
    return !reduce.matches || root.classList.contains('motion-preview');
  }

  // Up and Down move focus through the menu. Tab still leaves the menu.
  if (nav && links.length) {
    nav.addEventListener('keydown', function (event) {
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;

      var current = links.indexOf(document.activeElement);
      if (current === -1) return;

      var target = null;
      if (event.key === 'ArrowDown' || event.key === 'ArrowRight') {
        target = (current + 1) % links.length;
      } else if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') {
        target = (current - 1 + links.length) % links.length;
      } else if (event.key === 'Home') {
        target = 0;
      } else if (event.key === 'End') {
        target = links.length - 1;
      }

      if (target !== null) {
        event.preventDefault();
        links[target].focus();
      }
    });
  }

  document.addEventListener('click', function (event) {
    var link = event.target && event.target.closest ? event.target.closest('a[data-transition]') : null;
    if (!link || navigating) return;
    if (event.defaultPrevented || event.button !== 0) return;
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

    var url = new URL(link.href, window.location.href);
    if (url.origin !== window.location.origin) return;
    if (url.pathname === window.location.pathname && url.hash) return;

    event.preventDefault();
    navigating = true;

    try {
      window.sessionStorage.setItem('p3t-transition', '1');
    } catch (error) {
      /* Storage can be blocked. The link still works. */
    }

    if (!overlay) {
      window.location.href = url.href;
      return;
    }

    overlay.classList.add('is-covering');
    window.setTimeout(function () {
      window.location.href = url.href;
    }, motionAllowed() ? 500 : 240);
  });

  // Clear the entry class after the reveal so it cannot replay.
  if (root.classList.contains('wipe-enter')) {
    window.setTimeout(function () {
      root.classList.remove('wipe-enter');
    }, motionAllowed() ? 720 : 320);
  }

  // The two portrait layers turn toward the mouse at different rates. Pointer
  // values are interpolated once per frame, so a moving mouse does not restart
  // a CSS transition on every event. Touch screens and reduced motion stay still.
  var figure = document.querySelector('.hero__figure');
  var finePointer = window.matchMedia('(hover: hover) and (pointer: fine)');

  if (figure && finePointer.matches) {
    var lookTargetX = 0;
    var lookTargetY = 0;
    var lookCurrentX = 0;
    var lookCurrentY = 0;
    var lookFrame = 0;

    function applyLook() {
      figure.style.setProperty('--look-x', lookCurrentX.toFixed(4));
      figure.style.setProperty('--look-y', lookCurrentY.toFixed(4));
    }

    function runLook() {
      lookFrame = 0;
      lookCurrentX += (lookTargetX - lookCurrentX) * 0.16;
      lookCurrentY += (lookTargetY - lookCurrentY) * 0.16;
      if (Math.abs(lookTargetX - lookCurrentX) < 0.0005) lookCurrentX = lookTargetX;
      if (Math.abs(lookTargetY - lookCurrentY) < 0.0005) lookCurrentY = lookTargetY;
      applyLook();
      if (root.style.getPropertyValue('--ray-opacity') === '1') aimRay();
      if (lookCurrentX !== lookTargetX || lookCurrentY !== lookTargetY) {
        lookFrame = window.requestAnimationFrame(runLook);
      }
    }

    function scheduleLook() {
      if (!lookFrame) lookFrame = window.requestAnimationFrame(runLook);
    }

    resetGaze = function () {
      lookTargetX = 0;
      lookTargetY = 0;
      lookCurrentX = 0;
      lookCurrentY = 0;
      if (lookFrame) {
        window.cancelAnimationFrame(lookFrame);
        lookFrame = 0;
      }
      applyLook();
      aimRay();
    };

    window.addEventListener('pointermove', function (event) {
      if (event.pointerType !== 'mouse' || !motionAllowed()) return;
      var rect = figure.getBoundingClientRect();
      var midX = rect.left + rect.width / 2;
      var midY = rect.top + rect.height / 2;
      lookTargetX = Math.max(-1, Math.min(1, (event.clientX - midX) / (window.innerWidth / 2)));
      lookTargetY = Math.max(-1, Math.min(1, (event.clientY - midY) / (window.innerHeight / 2)));
      scheduleLook();
    }, { passive: true });

    document.addEventListener('mouseleave', resetGaze);

    if (reduce.addEventListener) {
      reduce.addEventListener('change', function () {
        if (reduce.matches && !root.classList.contains('motion-preview')) resetGaze();
      });
    }
  }

  // A short lock-on marker leaves the goggles and stops at the edge of the
  // active menu option, so the lettering stays clear. On the index it appears
  // while hovering or focusing a link. On a destination page it rests on the
  // current page and follows whichever link is being used.
  var heroImage = figure ? (figure.querySelector('.hero__layer--head .hero__image') || figure.querySelector('.hero__fallback')) : null;
  var headLayer = figure ? figure.querySelector('.hero__layer--head') : null;
  if (figure && heroImage) {
    var ray = document.createElement('div');
    ray.className = 'gaze-ray';
    ray.setAttribute('aria-hidden', 'true');
    var spark = document.createElement('div');
    spark.className = 'gaze-spark';
    spark.setAttribute('aria-hidden', 'true');
    var target = document.createElement('div');
    target.className = 'gaze-target';
    target.setAttribute('aria-hidden', 'true');
    document.body.appendChild(ray);
    document.body.appendChild(spark);
    document.body.appendChild(target);

    var restingLink = nav ? nav.querySelector('a[aria-current="page"], .is-selected') : null;
    var activeLink = null;

    function transformMatrix(element) {
      var value = element ? window.getComputedStyle(element).transform : 'none';
      if (!value || value === 'none') return null;
      if (typeof DOMMatrixReadOnly === 'function') return new DOMMatrixReadOnly(value);
      if (typeof WebKitCSSMatrix === 'function') return new WebKitCSSMatrix(value);
      return null;
    }

    function transformOrigin(element) {
      var parts = window.getComputedStyle(element).transformOrigin.split(/\s+/);
      function read(part, size) {
        if (part.indexOf('%') !== -1) return parseFloat(part) / 100 * size;
        return parseFloat(part) || 0;
      }
      return {
        x: read(parts[0], element.offsetWidth),
        y: read(parts[1], element.offsetHeight)
      };
    }

    function gogglePoint() {
      var rect = figure.getBoundingClientRect();
      var scale = Math.min(rect.width / heroImage.naturalWidth, rect.height / heroImage.naturalHeight);
      var paintedHeight = heroImage.naturalHeight * scale;
      var x = 556 * scale;
      var y = rect.height - paintedHeight + 461 * scale;

      if (headLayer) {
        var matrix = transformMatrix(headLayer);
        if (matrix) {
          var origin = transformOrigin(headLayer);
          var dx = x - origin.x;
          var dy = y - origin.y;
          x = origin.x + matrix.a * dx + matrix.c * dy + matrix.e;
          y = origin.y + matrix.b * dx + matrix.d * dy + matrix.f;
        }
      }

      return { x: rect.left + x, y: rect.top + y };
    }

    aimRay = function () {
      var link = activeLink || restingLink;
      if (!link) {
        root.style.setProperty('--ray-opacity', '0');
        return;
      }
      var source = gogglePoint();
      var box = link.getBoundingClientRect();
      var targetX = box.left - 14;
      var targetY = box.top + box.height / 2;
      var dx = targetX - source.x;
      var dy = targetY - source.y;
      root.style.setProperty('--ray-x', source.x.toFixed(1) + 'px');
      root.style.setProperty('--ray-y', source.y.toFixed(1) + 'px');
      root.style.setProperty('--ray-target-x', targetX.toFixed(1) + 'px');
      root.style.setProperty('--ray-target-y', targetY.toFixed(1) + 'px');
      root.style.setProperty('--ray-length', Math.sqrt(dx * dx + dy * dy).toFixed(1) + 'px');
      root.style.setProperty('--ray-angle', (Math.atan2(dy, dx) * 180 / Math.PI).toFixed(2) + 'deg');
      root.style.setProperty('--ray-opacity', '1');
    };

    if (links.length) {
      links.forEach(function (link) {
        link.addEventListener('mouseenter', function () { activeLink = link; aimRay(); });
        link.addEventListener('mouseleave', function () { activeLink = null; aimRay(); });
        link.addEventListener('focus', function () { activeLink = link; aimRay(); });
        link.addEventListener('blur', function () { activeLink = null; aimRay(); });
      });
    }

    window.addEventListener('resize', aimRay);
    window.addEventListener('scroll', aimRay, { passive: true });
    aimRay();
  }

  // The preview toggle is an explicit opt-in. It never changes the system
  // preference, only this session, so the reduced-motion default stays intact.
  if (motionToggle) {
    function syncMotionToggle() {
      var on = root.classList.contains('motion-preview');
      motionToggle.setAttribute('aria-pressed', on ? 'true' : 'false');
      motionToggle.textContent = on ? 'Motion: On' : 'Motion: Off';
    }

    syncMotionToggle();

    motionToggle.addEventListener('click', function () {
      var on = !root.classList.contains('motion-preview');
      root.classList.toggle('motion-preview', on);
      try {
        window.sessionStorage.setItem('p3t-motion-preview', on ? '1' : '0');
      } catch (error) {
        /* Storage can be blocked. The toggle still works for this page. */
      }
      syncMotionToggle();
      if (on) aimRay(); else resetGaze();
    });
  }
})();
