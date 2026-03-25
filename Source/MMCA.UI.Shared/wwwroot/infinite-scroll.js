const observers = new Map();

export function observe(dotNetRef, sentinelElement, id) {
    unobserve(id);

    const observer = new IntersectionObserver(entries => {
        if (entries[0].isIntersecting) {
            dotNetRef.invokeMethodAsync('OnSentinelVisible');
        }
    }, { rootMargin: '200px' });

    observer.observe(sentinelElement);
    observers.set(id, observer);
}

export function unobserve(id) {
    const observer = observers.get(id);
    if (observer) {
        observer.disconnect();
        observers.delete(id);
    }
}
