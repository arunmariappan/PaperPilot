// Enter asks the question and Shift+Enter starts a new line, as in the Gradio app.
document.addEventListener('keydown', event => {
  const box = event.target;
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing
    || !(box instanceof HTMLTextAreaElement) || !box.hasAttribute('data-enter-submits')) {
    return;
  }

  event.preventDefault();
  box.form?.requestSubmit();
});
