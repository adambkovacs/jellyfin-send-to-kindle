const toggle = document.querySelector(".motion-toggle");
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const preferenceKey = "books-to-kindle-motion";
const illustrations = [...document.querySelectorAll("[data-motion-source]")];
const staticSources = new Map(illustrations.map(image => [image, image.getAttribute("src")]));
const visibleIllustrations = new Set();
let allowMotion = true;

try {
  allowMotion = localStorage.getItem(preferenceKey) !== "off";
} catch {
  // The toggle is still usable if browser settings block storage.
}

function playIllustration(image) {
  const source = image.dataset.motionSource;
  if (image.getAttribute("src") !== source) image.src = source;
}

function stopIllustration(image) {
  const still = staticSources.get(image);
  if (image.getAttribute("src") !== still) image.src = still;
}

function updateMotion() {
  const playing = allowMotion && !reducedMotion.matches;
  document.documentElement.dataset.motion = playing ? "on" : "off";
  toggle.hidden = reducedMotion.matches;
  toggle.setAttribute("aria-pressed", String(playing));

  if (playing) {
    visibleIllustrations.forEach(playIllustration);
  } else {
    illustrations.forEach(stopIllustration);
  }
}

toggle.addEventListener("click", () => {
  allowMotion = !allowMotion;
  try {
    localStorage.setItem(preferenceKey, allowMotion ? "on" : "off");
  } catch {
    // Keep the in-page preference if browser settings block storage.
  }
  updateMotion();
});

reducedMotion.addEventListener("change", updateMotion);
updateMotion();

if ("IntersectionObserver" in window) {
  const reveals = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) {
        if (document.documentElement.dataset.motion === "on") {
          entry.target.classList.add("has-entered");
        }
        reveals.unobserve(entry.target);
      }
    }
  }, { threshold: 0.12 });

  const artwork = new IntersectionObserver(entries => {
    for (const { target, isIntersecting } of entries) {
      if (isIntersecting) {
        visibleIllustrations.add(target);
        if (document.documentElement.dataset.motion === "on") playIllustration(target);
      } else {
        visibleIllustrations.delete(target);
        stopIllustration(target);
      }
    }
  }, { threshold: 0.18 });

  document.querySelectorAll("[data-reveal]").forEach(element => reveals.observe(element));
  illustrations.forEach(image => artwork.observe(image));
} else {
  illustrations.forEach(image => visibleIllustrations.add(image));
  updateMotion();
}
