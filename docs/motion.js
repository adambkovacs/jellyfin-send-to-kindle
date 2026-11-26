const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const illustrations = [...document.querySelectorAll("[data-motion-source]")];
const stillSources = new Map(illustrations.map(image => [image, image.getAttribute("src")]));
const visible = new Set();
const played = new Set();

function playOnce(image) {
  if (reducedMotion.matches || played.has(image)) return;
  image.src = image.dataset.motionSource;
  played.add(image);
}

function updateMotion() {
  document.documentElement.dataset.motion = reducedMotion.matches ? "off" : "on";
  if (reducedMotion.matches) {
    illustrations.forEach(image => { image.src = stillSources.get(image); });
  } else {
    visible.forEach(playOnce);
  }
}

reducedMotion.addEventListener("change", updateMotion);
updateMotion();

if ("IntersectionObserver" in window) {
  const observer = new IntersectionObserver(entries => {
    for (const { target, isIntersecting } of entries) {
      if (!isIntersecting) {
        visible.delete(target);
        continue;
      }
      if (target.hasAttribute("data-motion-source")) {
        visible.add(target);
        playOnce(target);
      } else {
        if (!reducedMotion.matches) target.classList.add("has-entered");
        observer.unobserve(target);
      }
    }
  }, { threshold: 0.18 });
  document.querySelectorAll("[data-reveal], [data-motion-source]").forEach(element => observer.observe(element));
} else {
  illustrations.forEach(playOnce);
}
