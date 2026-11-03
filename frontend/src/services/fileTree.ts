export interface FileNode {
  name: string
  path: string
  folders: FileNode[]
  files: { name: string; path: string }[]
}

export function buildFileTree(paths: string[]): FileNode {
  const root: FileNode = { name: '', path: '', folders: [], files: [] }
  for (const path of [...new Set(paths)].sort((a, b) => a.localeCompare(b))) {
    const parts = path.split('/')
    let folder = root
    for (const part of parts.slice(0, -1)) {
      let child = folder.folders.find((item) => item.name === part)
      if (!child) {
        child = { name: part, path: folder.path ? `${folder.path}/${part}` : part, folders: [], files: [] }
        folder.folders.push(child)
      }
      folder = child
    }
    folder.files.push({ name: parts.at(-1)!, path })
  }
  const sort = (node: FileNode) => {
    node.folders.sort((a, b) => a.name.localeCompare(b.name))
    node.files.sort((a, b) => a.name.localeCompare(b.name))
    node.folders.forEach(sort)
  }
  sort(root)
  return root
}
