// Soft chroma key for screenshots taken on a plain green (#00FF00)
// wallpaper: fully green pixels vanish, the anti-aliased edges fade out
// and lose their green tint. Every image matching selector is replaced by
// a canvas holding rows top..bottom of its data-crop="top,bottom" (in
// sourceWidth pixels), keeping the image's class and id.
function keyOut(img, sourceWidth) {
  const [cropTop, cropBottom] = img.dataset.crop.split(',').map(Number);
  const scale = img.naturalWidth / sourceWidth;
  const top = Math.round(cropTop * scale);
  const bottom = Math.min(img.naturalHeight, Math.round(cropBottom * scale));
  const canvas = document.createElement('canvas');
  canvas.width = img.naturalWidth;
  canvas.height = bottom - top;
  canvas.className = img.className;
  canvas.id = img.id;

  const ctx = canvas.getContext('2d');
  ctx.drawImage(img, 0, top, canvas.width, canvas.height, 0, 0, canvas.width, canvas.height);

  const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
  const px = image.data;
  for (let i = 0; i < px.length; i += 4) {
    const r = px[i], g = px[i + 1], b = px[i + 2];
    const green = g - Math.max(r, b);
    if (green <= 40) continue;
    px[i + 3] = green >= 120 ? 0 : Math.round(255 * (1 - (green - 40) / 80));
    px[i + 1] = Math.max(r, b);
  }
  ctx.putImageData(image, 0, 0);
  img.replaceWith(canvas);
}

function keyAll(selector, sourceWidth) {
  for (const img of document.querySelectorAll(selector)) {
    if (img.complete) keyOut(img, sourceWidth);
    else img.onload = () => keyOut(img, sourceWidth);
  }
}
