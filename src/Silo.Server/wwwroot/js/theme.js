// Theme before first paint: saved choice, else the system setting. (Its own file: the page's
// Content-Security-Policy runs no inline script.)
(function () {
  var t = null; try { t = localStorage.getItem("silo.theme"); } catch (e) {}
  if (!t) t = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  document.documentElement.setAttribute("data-theme", t);
})();
