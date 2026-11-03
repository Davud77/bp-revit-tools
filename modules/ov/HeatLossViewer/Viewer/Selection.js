globalThis.ObjectSelection = class {
  constructor(){this.items=new Map();this.active=null;}
  has(key){return this.items.has(key);}
  clear(){this.items.clear();this.active=null;}
  apply(item,event={}) {
    if(!item){if(!event.ctrlKey && !event.metaKey && !event.shiftKey)this.clear();return this.current();}
    if(event.shiftKey){this.items.delete(item.key);if(this.active===item.key)this.active=[...this.items.keys()].at(-1)??null;}
    else {if(!event.ctrlKey && !event.metaKey)this.items.clear();this.items.set(item.key,item);this.active=item.key;}
    return this.current();
  }
  current(){return this.items.get(this.active) || null;}
};
